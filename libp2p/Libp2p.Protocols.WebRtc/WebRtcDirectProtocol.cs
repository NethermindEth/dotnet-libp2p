// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;
using SIPSorcery.Net;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.WebRtc.Tests")]

namespace Nethermind.Libp2p.Protocols.WebRtc;

public class WebRtcDirectProtocol : ITransportProtocol
{
    private const string NoiseChannelLabel = "noise";
    private const string OfferPrefix = "WRTC_OFFER\n";
    private const string AnswerPrefix = "WRTC_ANSWER\n";
    private const int MaxConcurrentIncomingOffers = 128;

    private readonly ILogger<WebRtcDirectProtocol>? _logger;
    private readonly WebRtcDirectReplayWindow _replayWindow = new();
    private readonly WebRtcDirectRateLimiter _offerRateLimiter = new();
    private readonly SemaphoreSlim _incomingOfferSlots = new(MaxConcurrentIncomingOffers, MaxConcurrentIncomingOffers);
    private readonly RTCCertificate2 _certificate;
    private readonly DtlsFingerprint _fingerprint;
    private readonly Func<RTCPeerConnection, RTCDtlsFingerprint?> _readRemoteFingerprint;

    public WebRtcDirectProtocol(ILoggerFactory? loggerFactory = null)
        : this(pc => pc.RemotePeerDtlsFingerprint, loggerFactory)
    {
    }

    internal WebRtcDirectProtocol(Func<RTCPeerConnection, RTCDtlsFingerprint?> readRemoteFingerprint, ILoggerFactory? loggerFactory = null)
    {
        _readRemoteFingerprint = readRemoteFingerprint;
        _logger = loggerFactory?.CreateLogger<WebRtcDirectProtocol>();
        (_certificate, _fingerprint) = CreateLocalCertificate();
    }

    public string Id => "webrtc-direct";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];

    public static bool IsAddressMatch(Multiaddress addr) => WebRtcDirectMultiaddr.IsWebRtcDirect(addr);

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        IPEndPoint endpoint = listenAddr.ToEndPoint();
        using UdpClient udp = new(endpoint);
        if (endpoint.Port == 0)
        {
            endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        }

        Multiaddress listenerAddr = WebRtcDirectMultiaddr.Build(endpoint, _fingerprint);
        context.ListenerReady(listenerAddr);

        _logger?.LogInformation("WebRTC-Direct listening on {Endpoint}", endpoint);

        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await udp.ReceiveAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string message = Encoding.UTF8.GetString(packet.Buffer);
            if (!message.StartsWith(OfferPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_offerRateLimiter.TryAccept(packet.RemoteEndPoint, packet.Buffer.Length, out string? rejectReason))
            {
                _logger?.LogDebug("Dropped WebRTC-Direct offer from {Remote}: {Reason}", packet.RemoteEndPoint, rejectReason);
                continue;
            }

            if (!_incomingOfferSlots.Wait(0))
            {
                _logger?.LogDebug("Dropped WebRTC-Direct offer from {Remote}: listener at max concurrent offer handling ({MaxConcurrent}).",
                    packet.RemoteEndPoint,
                    MaxConcurrentIncomingOffers);
                continue;
            }

            string signedOfferPayload = message[OfferPrefix.Length..];
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleIncomingOfferAsync(context, endpoint, udp, packet.RemoteEndPoint, signedOfferPayload, token);
                }
                finally
                {
                    _incomingOfferSlots.Release();
                }
            }, token);
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        (IPEndPoint endpoint, DtlsFingerprint expectedFingerprint) = WebRtcDirectMultiaddr.Parse(remoteAddr);

        RTCPeerConnection pc = CreatePeerConnection();

        try
        {
            RTCDataChannel noiseDataChannel = await pc.createDataChannel(NoiseChannelLabel, new RTCDataChannelInit { negotiated = true, id = 0 });

            Task<RTCDataChannel> noiseOpenTask = WaitForDataChannelOpenAsync(noiseDataChannel, token);
            Task connectionTask = WaitForConnectionAsync(pc, token);

            RTCSessionDescriptionInit offer = pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
            ValidateExpectedFingerprint(WebRtcDirectSdp.ExtractFingerprint(offer.sdp ?? string.Empty), _fingerprint, "offer");
            await pc.setLocalDescription(offer);

            // webrtc-direct has no signalling channel: the offer is never sent anywhere. The answer
            // is synthesised locally from the multiaddr, and the listener identifies this dial from
            // the STUN USERNAME alone, which carries the server ufrag built below.
            RTCSessionDescriptionInit answer = new()
            {
                type = RTCSdpType.answer,
                sdp = WebRtcDirectAnswerSdp.Build(endpoint, expectedFingerprint, pc.GetRtpChannel().LocalIcePassword),
            };
            EnsureRemoteDescriptionApplied(pc.setRemoteDescription(answer), "answer");

            await connectionTask;
            RTCDataChannel openedNoiseDataChannel = await noiseOpenTask;

            // The answer above only claims the /certhash; this checks the certificate the peer
            // actually presented in the DTLS handshake against it. Read through the accessor
            // injected by #422 so the enforcement stays coverable in CI without a live peer.
            ValidateRemoteFingerprint(_readRemoteFingerprint(pc), expectedFingerprint);

            using INewConnectionContext connectionContext = context.CreateConnection();
            using CancellationTokenRegistration closed = connectionContext.Token.Register(pc.close);
            connectionContext.State.LocalAddress = pc.GetRtpChannel().RTPLocalEndPoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = endpoint.ToMultiaddress(ProtocolType.Udp);

            // webrtc-direct.md, Connection Security: the Noise handshake runs "over this channel"
            // using "the message framing described in the Multiplexing section", not as raw
            // datachannel messages. So it goes through WebRtcStream like any other stream; sending
            // it unframed is invisible to a self-dial and interoperates with nothing.
            RtcDataChannelAdapter noiseAdapter = new(openedNoiseDataChannel);
            await using WebRtcStream noiseStream = new(noiseAdapter);
            WebRtcStreamChannel handshakeChannel = new(noiseStream);
            // The dialer is _A_, the Noise RESPONDER. Both role-dependent values here come from
            // WebRtcDirectRoles, which carries the spec sentence behind each one; getting either
            // backwards is invisible to a loopback test, since both halves would be wrong
            // together, and fails against every real peer.
            (DtlsFingerprint dialerFingerprint, DtlsFingerprint listenerFingerprint) =
                WebRtcDirectRoles.OrderFingerprints(isListener: false, local: _fingerprint, remote: expectedFingerprint);
            byte[] prologue = WebRtcNoisePrologue.Build(dialerFingerprint, listenerFingerprint);
            (IChannel encryptedChannel, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
                handshakeChannel, context.Peer.Identity, prologue, isInitiator: !WebRtcDirectRoles.ListenerIsNoiseInitiator, token);
            connectionContext.State.RemotePublicKey = remoteKey;
            await connectionContext.Upgrade(encryptedChannel);
        }
        finally
        {
            pc.close();
            pc.Dispose();
        }
    }

    private async Task HandleIncomingOfferAsync(
        ITransportContext context,
        IPEndPoint localEndpoint,
        UdpClient signalingUdp,
        IPEndPoint remoteEndpoint,
        string signedOfferPayload,
        CancellationToken token)
    {
        RTCPeerConnection pc = CreatePeerConnection();

        try
        {
            (string offerSessionId, string remoteOfferSdp, Identity _) = WebRtcDirectSignaling.ParseAndValidate(
                signedOfferPayload,
                WebRtcDirectSignalType.Offer,
                expectedSessionId: null,
                _replayWindow);
            RTCSessionDescriptionInit offer = new() { type = RTCSdpType.offer, sdp = remoteOfferSdp };
            DtlsFingerprint offeredFingerprint = WebRtcDirectSdp.ExtractFingerprint(remoteOfferSdp);
            EnsureRemoteDescriptionApplied(pc.setRemoteDescription(offer), "offer");

            RTCDataChannel noiseChannel = await pc.createDataChannel(NoiseChannelLabel, new RTCDataChannelInit { negotiated = true, id = 0 });
            Task<RTCDataChannel> noiseOpenTask = WaitForDataChannelOpenAsync(noiseChannel, token);

            RTCSessionDescriptionInit answer = pc.createAnswer();
            ValidateExpectedFingerprint(WebRtcDirectSdp.ExtractFingerprint(answer.sdp ?? string.Empty), _fingerprint, "answer");
            string signedAnswer = WebRtcDirectSignaling.BuildSignedPayload(
                WebRtcDirectSignalType.Answer,
                context.Peer.Identity,
                offerSessionId,
                answer.sdp ?? string.Empty);
            await pc.setLocalDescription(answer);

            byte[] answerBytes = Encoding.UTF8.GetBytes(AnswerPrefix + signedAnswer);
            await signalingUdp.SendAsync(answerBytes, answerBytes.Length, remoteEndpoint);

            await WaitForConnectionAsync(pc, token);
            ValidateRemoteFingerprint(_readRemoteFingerprint(pc), offeredFingerprint);
            RTCDataChannel openedNoiseChannel = await noiseOpenTask;

            using INewConnectionContext connectionContext = context.CreateConnection();
            using CancellationTokenRegistration closed = connectionContext.Token.Register(pc.close);
            connectionContext.State.LocalAddress = localEndpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = remoteEndpoint.ToMultiaddress(ProtocolType.Udp);

            DataChannelOverIChannel rawChannel = new(openedNoiseChannel);
            // The listener is _B_, the Noise INITIATOR. See WebRtcDirectRoles for why.
            (DtlsFingerprint dialerFingerprint, DtlsFingerprint listenerFingerprint) =
                WebRtcDirectRoles.OrderFingerprints(isListener: true, local: _fingerprint, remote: offeredFingerprint);
            byte[] prologue = WebRtcNoisePrologue.Build(dialerFingerprint, listenerFingerprint);
            (IChannel encryptedChannel, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
                rawChannel, context.Peer.Identity, prologue, isInitiator: WebRtcDirectRoles.ListenerIsNoiseInitiator, token);
            connectionContext.State.RemotePublicKey = remoteKey;
            await connectionContext.Upgrade(encryptedChannel);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed handling incoming WebRTC-Direct offer from {Remote}", remoteEndpoint);
        }
        finally
        {
            pc.close();
            pc.Dispose();
        }
    }

    private RTCPeerConnection CreatePeerConnection()
    {
        RTCConfiguration config = new()
        {
            X_ICEIncludeAllInterfaceAddresses = false,
            X_GatherTimeoutMs = 2000,
            certificates2 = [_certificate],
        };

        return new RTCPeerConnection(config);
    }

    private static (RTCCertificate2 Certificate, DtlsFingerprint Fingerprint) CreateLocalCertificate()
    {
        (Certificate certificateChain, AsymmetricKeyParameter privateKey) =
            DtlsUtils.CreateSelfSignedTlsCert(new BcTlsCrypto());
        X509Certificate certificate = new(certificateChain.GetCertificateAt(0).GetEncoded());
        RTCCertificate2 rtcCertificate = new()
        {
            Certificate = certificate,
            PrivateKey = privateKey,
        };

        return (rtcCertificate, DtlsFingerprint.FromRtcFingerprint(DtlsUtils.Fingerprint(certificateChain)));
    }

    private static Task WaitForConnectionAsync(RTCPeerConnection pc, CancellationToken token)
    {
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
            {
                connected.TrySetResult();
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected)
            {
                connected.TrySetException(new InvalidOperationException($"WebRTC connection state: {state}"));
            }
        };

        token.Register(() => connected.TrySetCanceled(token));
        return connected.Task;
    }

    private static Task<RTCDataChannel> WaitForDataChannelOpenAsync(RTCDataChannel channel, CancellationToken token)
    {
        TaskCompletionSource<RTCDataChannel> opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.onopen += () => opened.TrySetResult(channel);
        channel.onclose += () => opened.TrySetException(new InvalidOperationException("Noise data channel closed before opening."));
        token.Register(() => opened.TrySetCanceled(token));
        return opened.Task;
    }

    internal static void ValidateRemoteFingerprint(RTCDtlsFingerprint? remote, DtlsFingerprint expected)
    {
        if (remote is null || !expected.Matches(remote))
        {
            throw new InvalidOperationException("Remote DTLS fingerprint does not match /certhash.");
        }
    }

    private static void ValidateExpectedFingerprint(DtlsFingerprint actual, DtlsFingerprint expected, string source)
    {
        bool algorithmMatches = actual.Algorithm.Equals(expected.Algorithm, StringComparison.OrdinalIgnoreCase);
        bool digestMatches = CryptographicOperations.FixedTimeEquals(actual.Value, expected.Value);
        if (!algorithmMatches || !digestMatches)
        {
            throw new InvalidOperationException($"DTLS fingerprint mismatch in {source} SDP.");
        }
    }

    private static void EnsureRemoteDescriptionApplied(SetDescriptionResultEnum result, string source)
    {
        if (result != SetDescriptionResultEnum.OK)
        {
            throw new InvalidOperationException($"Failed to apply WebRTC remote {source} SDP: {result}.");
        }
    }

}
