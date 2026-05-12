// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using Org.BouncyCastle.Crypto;
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

    public WebRtcDirectProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<WebRtcDirectProtocol>();
        (_certificate, _fingerprint) = CreateLocalCertificate();
    }

    public string Id => "webrtc-direct";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];

    public static bool IsAddressMatch(Multiaddress addr) => WebRtcDirectMultiaddr.IsWebRtcDirect(addr);

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        MultiaddrProtocolRegistry.EnsureRegistered();

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
        MultiaddrProtocolRegistry.EnsureRegistered();

        (IPEndPoint endpoint, DtlsFingerprint expectedFingerprint) = WebRtcDirectMultiaddr.Parse(remoteAddr);

        using UdpClient signalingUdp = new(0);
        RTCPeerConnection pc = CreatePeerConnection();
        string signalingSessionId = WebRtcDirectSignaling.NewSessionId();

        try
        {
            RTCDataChannel noiseDataChannel = await pc.createDataChannel(NoiseChannelLabel, new RTCDataChannelInit { negotiated = true, id = 0 });

            Task<RTCDataChannel> noiseOpenTask = WaitForDataChannelOpenAsync(noiseDataChannel, token);
            Task connectionTask = WaitForConnectionAsync(pc, token);

            RTCSessionDescriptionInit offer = pc.createOffer(new RTCOfferOptions { X_WaitForIceGatheringToComplete = true });
            ValidateExpectedFingerprint(WebRtcDirectSdp.ExtractFingerprint(offer.sdp ?? string.Empty), _fingerprint, "offer");
            string signedOffer = WebRtcDirectSignaling.BuildSignedPayload(
                WebRtcDirectSignalType.Offer,
                context.Peer.Identity,
                signalingSessionId,
                offer.sdp ?? string.Empty);

            await pc.setLocalDescription(offer);
            await signalingUdp.SendAsync(Encoding.UTF8.GetBytes(OfferPrefix + signedOffer), endpoint, token);

            RTCSessionDescriptionInit answer = await ReceiveAnswerAsync(signalingUdp, endpoint, signalingSessionId, token);
            DtlsFingerprint answerFingerprint = WebRtcDirectSdp.ExtractFingerprint(answer.sdp ?? string.Empty);
            ValidateExpectedFingerprint(answerFingerprint, expectedFingerprint, "answer");
            EnsureRemoteDescriptionApplied(pc.setRemoteDescription(answer), "answer");

            await connectionTask;
            RTCDataChannel openedNoiseDataChannel = await noiseOpenTask;

            ValidateRemoteFingerprint(pc, expectedFingerprint);

            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = signalingUdp.Client.LocalEndPoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = endpoint.ToMultiaddress(ProtocolType.Udp);

            DataChannelOverIChannel rawChannel = new(openedNoiseDataChannel);
            byte[] prologue = WebRtcNoisePrologue.Build(_fingerprint, expectedFingerprint);
            (IChannel encryptedChannel, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
                rawChannel, context.Peer.Identity, prologue, isInitiator: true, token);
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
            ValidateRemoteFingerprint(pc, offeredFingerprint);
            RTCDataChannel openedNoiseChannel = await noiseOpenTask;

            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = localEndpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = remoteEndpoint.ToMultiaddress(ProtocolType.Udp);

            DataChannelOverIChannel rawChannel = new(openedNoiseChannel);
            byte[] prologue = WebRtcNoisePrologue.Build(offeredFingerprint, _fingerprint);
            (IChannel encryptedChannel, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
                rawChannel, context.Peer.Identity, prologue, isInitiator: false, token);
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
        (X509Certificate certificate, AsymmetricKeyParameter privateKey) = DtlsUtils.CreateSelfSignedEcdsaCert();
        RTCCertificate2 rtcCertificate = new()
        {
            Certificate = certificate,
            PrivateKey = privateKey,
        };

        return (rtcCertificate, DtlsFingerprint.FromRtcFingerprint(DtlsUtils.Fingerprint(certificate)));
    }

    private async Task<RTCSessionDescriptionInit> ReceiveAnswerAsync(
        UdpClient udp,
        IPEndPoint expectedRemoteEndpoint,
        string expectedSessionId,
        CancellationToken token)
    {
        for (; ; )
        {
            UdpReceiveResult packet = await udp.ReceiveAsync(token);
            if (!packet.RemoteEndPoint.Equals(expectedRemoteEndpoint))
            {
                continue;
            }

            string msg = Encoding.UTF8.GetString(packet.Buffer);
            if (!msg.StartsWith(AnswerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            (string _, string answerSdp, Identity _) = WebRtcDirectSignaling.ParseAndValidate(
                msg[AnswerPrefix.Length..],
                WebRtcDirectSignalType.Answer,
                expectedSessionId,
                _replayWindow);

            return new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
        }
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

    private static void ValidateRemoteFingerprint(RTCPeerConnection pc, DtlsFingerprint expected)
    {
        RTCDtlsFingerprint? remote = pc.RemotePeerDtlsFingerprint;
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
