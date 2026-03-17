// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
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

    private readonly ILogger<WebRtcDirectProtocol>? _logger;

    public WebRtcDirectProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<WebRtcDirectProtocol>();
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

        DtlsFingerprint listenerFingerprint = await ProbeListenerFingerprintAsync(token);
        Multiaddress listenerAddr = WebRtcDirectMultiaddr.Build(endpoint, listenerFingerprint);
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

            string remoteOfferSdp = message[OfferPrefix.Length..];
            _ = Task.Run(() => HandleIncomingOfferAsync(context, endpoint, udp, packet.RemoteEndPoint, remoteOfferSdp, token), token);
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        MultiaddrProtocolRegistry.EnsureRegistered();

        (IPEndPoint endpoint, DtlsFingerprint expectedFingerprint) = WebRtcDirectMultiaddr.Parse(remoteAddr);

        using UdpClient signalingUdp = new(0);
        RTCPeerConnection pc = CreatePeerConnection();

        try
        {
            RTCDataChannel noiseDataChannel = await pc.createDataChannel(NoiseChannelLabel, new RTCDataChannelInit());

            Task<RTCDataChannel> noiseOpenTask = WaitForDataChannelOpenAsync(noiseDataChannel, token);
            Task connectionTask = WaitForConnectionAsync(pc, token);

            DtlsFingerprint dialerFingerprint = await ProbeLocalFingerprintAsync(pc, token);
            RTCSessionDescriptionInit offer = WebRtcDirectSdp.BuildOffer(endpoint, dialerFingerprint);

            await pc.setLocalDescription(offer);
            await signalingUdp.SendAsync(Encoding.UTF8.GetBytes(OfferPrefix + offer.sdp), endpoint, token);

            RTCSessionDescriptionInit answer = await ReceiveAnswerAsync(signalingUdp, token);
            DtlsFingerprint answerFingerprint = WebRtcDirectSdp.ExtractFingerprint(answer.sdp ?? string.Empty);
            ValidateExpectedFingerprint(answerFingerprint, expectedFingerprint, "answer");
            pc.setRemoteDescription(answer);

            await connectionTask;
            RTCDataChannel openedNoiseDataChannel = await noiseOpenTask;

            ValidateRemoteFingerprint(pc, expectedFingerprint);

            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = signalingUdp.Client.LocalEndPoint!.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = endpoint.ToMultiaddress(ProtocolType.Udp);

            DataChannelOverIChannel downChannel = new(openedNoiseDataChannel);
            await connectionContext.Upgrade(downChannel);
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
        string remoteOfferSdp,
        CancellationToken token)
    {
        RTCPeerConnection pc = CreatePeerConnection();

        try
        {
            RTCSessionDescriptionInit offer = new() { type = RTCSdpType.offer, sdp = remoteOfferSdp };
            DtlsFingerprint offeredFingerprint = WebRtcDirectSdp.ExtractFingerprint(remoteOfferSdp);
            pc.setRemoteDescription(offer);

            DtlsFingerprint localFingerprint = await ProbeLocalFingerprintAsync(pc, token);
            RTCSessionDescriptionInit answer = WebRtcDirectSdp.BuildAnswer(offer, localFingerprint);
            await pc.setLocalDescription(answer);

            byte[] answerBytes = Encoding.UTF8.GetBytes(AnswerPrefix + answer.sdp);
            await signalingUdp.SendAsync(answerBytes, answerBytes.Length, remoteEndpoint);

            RTCDataChannel noiseChannel = await WaitForRemoteNoiseDataChannelAsync(pc, token);
            await WaitForConnectionAsync(pc, token);
            ValidateRemoteFingerprint(pc, offeredFingerprint);

            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = localEndpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = remoteEndpoint.ToMultiaddress(ProtocolType.Udp);

            DataChannelOverIChannel downChannel = new(noiseChannel);
            await connectionContext.Upgrade(downChannel);
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

    private static RTCPeerConnection CreatePeerConnection()
    {
        RTCConfiguration config = new()
        {
            X_ICEIncludeAllInterfaceAddresses = false,
            X_GatherTimeoutMs = 2000,
        };

        return new RTCPeerConnection(config);
    }

    private static async Task<RTCSessionDescriptionInit> ReceiveAnswerAsync(UdpClient udp, CancellationToken token)
    {
        for (; ; )
        {
            UdpReceiveResult packet = await udp.ReceiveAsync(token);
            string msg = Encoding.UTF8.GetString(packet.Buffer);
            if (!msg.StartsWith(AnswerPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            return new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = msg[AnswerPrefix.Length..] };
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

    private static Task<RTCDataChannel> WaitForRemoteNoiseDataChannelAsync(RTCPeerConnection pc, CancellationToken token)
    {
        TaskCompletionSource<RTCDataChannel> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pc.ondatachannel += channel =>
        {
            if (channel.label == NoiseChannelLabel)
            {
                channel.onopen += () => tcs.TrySetResult(channel);
            }
        };
        token.Register(() => tcs.TrySetCanceled(token));
        return tcs.Task;
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

    private Task<DtlsFingerprint> ProbeLocalFingerprintAsync(RTCPeerConnection pc, CancellationToken token)
    {
        _ = token;
        if (TryGetLocalFingerprint(pc, out DtlsFingerprint? fingerprint))
        {
            _logger?.LogDebug("DTLS fingerprint resolved via DtlsCertificateFingerprint (pre-offer).");
            return Task.FromResult(fingerprint!);
        }

        _logger?.LogDebug("DtlsCertificateFingerprint unavailable; probing via createOffer SDP.");
        RTCSessionDescriptionInit probeOffer = pc.createOffer(null);
        pc.setLocalDescription(probeOffer);

        if (!string.IsNullOrWhiteSpace(probeOffer.sdp))
        {
            try
            {
                DtlsFingerprint fromSdp = WebRtcDirectSdp.ExtractFingerprint(probeOffer.sdp);
                _logger?.LogDebug("DTLS fingerprint resolved via SDP a=fingerprint line.");
                return Task.FromResult(fromSdp);
            }
            catch (FormatException ex)
            {
                _logger?.LogDebug(ex, "SDP probe did not contain an a=fingerprint line.");
            }
        }

        if (TryGetLocalFingerprint(pc, out fingerprint))
        {
            _logger?.LogDebug("DTLS fingerprint resolved via DtlsCertificateFingerprint (post-setLocalDescription).");
            return Task.FromResult(fingerprint!);
        }

        _logger?.LogWarning(
            "Host WebRTC stack did not expose a local DTLS fingerprint via any probe path " +
            "(DtlsCertificateFingerprint pre/post-offer, SDP a=fingerprint). " +
            "SIPSorcery may require an explicit certificate or a newer runtime.");
        throw new InvalidOperationException("Unable to determine local DTLS fingerprint from RTCPeerConnection.");
    }

    private static bool TryGetLocalFingerprint(RTCPeerConnection pc, out DtlsFingerprint? fingerprint)
    {
        fingerprint = null;
        if (pc.DtlsCertificateFingerprint is null)
        {
            return false;
        }

        try
        {
            fingerprint = DtlsFingerprint.FromRtcFingerprint(pc.DtlsCertificateFingerprint);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<DtlsFingerprint> ProbeListenerFingerprintAsync(CancellationToken token)
    {
        using RTCPeerConnection probe = CreatePeerConnection();
        return await ProbeLocalFingerprintAsync(probe, token);
    }
}