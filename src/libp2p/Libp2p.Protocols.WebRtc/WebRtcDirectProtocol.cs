// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Multiformats.Address;
using Multiformats.Address.Net;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.X509;
using SIPSorcery.Net;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.WebRtc.Tests")]

namespace Nethermind.Libp2p.Protocols.WebRtc;

public class WebRtcDirectProtocol : ITransportProtocol
{
    private const string NoiseChannelLabel = "noise";
    private const string OfferPrefix = "WRTC_OFFER\n";
    private const string AnswerPrefix = "WRTC_ANSWER\n";
    private const string IceCredentialPrefix = "libp2p+webrtc+v1/";
    private const int MaxConcurrentIncomingOffers = 128;

    private readonly ILogger<WebRtcDirectProtocol>? _logger;
    private readonly WebRtcDirectReplayWindow _replayWindow = new();
    private readonly WebRtcDirectRateLimiter _offerRateLimiter = new();
    private readonly SemaphoreSlim _incomingOfferSlots = new(MaxConcurrentIncomingOffers, MaxConcurrentIncomingOffers);
    private readonly RTCCertificate2? _certificate;
    private readonly DtlsFingerprint? _fingerprint;
    private readonly IJSRuntime? _jsRuntime;

    public WebRtcDirectProtocol(ILoggerFactory? loggerFactory = null, IJSRuntime? jsRuntime = null)
    {
        _logger = loggerFactory?.CreateLogger<WebRtcDirectProtocol>();
        _jsRuntime = jsRuntime;

        if (!OperatingSystem.IsBrowser())
        {
            (_certificate, _fingerprint) = CreateLocalCertificate();
        }
    }

    public string Id => "webrtc-direct";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];

    public static bool IsAddressMatch(Multiaddress addr) => WebRtcDirectMultiaddr.IsWebRtcDirect(addr);

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        if (OperatingSystem.IsBrowser())
        {
            await Task.Yield();
            throw new PlatformNotSupportedException("Browsers cannot listen on WebRTC-Direct addresses because they cannot open UDP sockets.");
        }

        IPEndPoint endpoint = listenAddr.ToEndPoint();
        using UdpClient udp = new(endpoint);
        if (endpoint.Port == 0)
        {
            endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
        }

        Multiaddress listenerAddr = WebRtcDirectMultiaddr.Build(endpoint, NativeFingerprint);
        if (listenAddr.Has<P2P>())
        {
            listenerAddr = listenerAddr.Add<P2P>(listenAddr.Get<P2P>().ToString());
        }

        context.ListenerReady(listenerAddr);

        _logger?.LogInformation("WebRTC-Direct listening on {Endpoint}", endpoint);
        ConcurrentDictionary<IPEndPoint, WebRtcDirectIceLiteConnection> iceLiteConnections = new();
        object udpSendLock = new();

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

            if (IsStunPacket(packet.Buffer))
            {
                await HandleIceLiteStunAsync(
                    context,
                    endpoint,
                    listenerAddr,
                    udp,
                    packet,
                    iceLiteConnections,
                    udpSendLock,
                    token);
                continue;
            }

            if (iceLiteConnections.TryGetValue(packet.RemoteEndPoint, out WebRtcDirectIceLiteConnection? iceConnection))
            {
                iceConnection.HandleDatagram(packet.Buffer);
                continue;
            }

            if (!TryReadPrefixedMessage(packet.Buffer, OfferPrefix, out string signedOfferPayload))
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

        foreach (WebRtcDirectIceLiteConnection connection in iceLiteConnections.Values)
        {
            await connection.DisposeAsync();
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        if (OperatingSystem.IsBrowser())
        {
            await DialBrowserAsync(context, remoteAddr, token);
            return;
        }

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
            ValidateExpectedFingerprint(WebRtcDirectSdp.ExtractFingerprint(offer.sdp ?? string.Empty), NativeFingerprint, "offer");
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

            DataChannelOverIChannel rawChannel = new(openedNoiseDataChannel);
            byte[] prologue = WebRtcNoisePrologue.Build(NativeFingerprint, expectedFingerprint);
            (IChannel encryptedChannel, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
                rawChannel, context.Peer.Identity, prologue, isInitiator: true, token);
            WebRtcProtocol.ValidateRemotePublicKey(remoteAddr, remoteKey);

            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = signalingUdp.Client.LocalEndPoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = endpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemotePublicKey = remoteKey;
            PeerId remotePeerId = new(remoteKey);
            if (remoteAddr.Has<P2P>())
            {
                connectionContext.State.RemoteAddress = remoteAddr;
            }
            else if (connectionContext.State.RemoteAddress is not null)
            {
                connectionContext.State.RemoteAddress = connectionContext.State.RemoteAddress.Add<P2P>(remotePeerId.ToString());
            }

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
            ValidateExpectedFingerprint(WebRtcDirectSdp.ExtractFingerprint(answer.sdp ?? string.Empty), NativeFingerprint, "answer");
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

            DataChannelOverIChannel rawChannel = new(openedNoiseChannel);
            byte[] prologue = WebRtcNoisePrologue.Build(offeredFingerprint, NativeFingerprint);
            (IChannel encryptedChannel, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
                rawChannel, context.Peer.Identity, prologue, isInitiator: false, token);

            INewConnectionContext connectionContext = context.CreateConnection();
            connectionContext.State.LocalAddress = localEndpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemoteAddress = remoteEndpoint.ToMultiaddress(ProtocolType.Udp);
            connectionContext.State.RemotePublicKey = remoteKey;
            PeerId remotePeerId = new(remoteKey);
            connectionContext.State.RemoteAddress = connectionContext.State.RemoteAddress.Add<P2P>(remotePeerId.ToString());
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
            certificates2 = [NativeCertificate],
        };

        return new RTCPeerConnection(config);
    }

    private DtlsFingerprint NativeFingerprint => _fingerprint ?? throw new InvalidOperationException("Native WebRTC-Direct certificate is not available.");
    private RTCCertificate2 NativeCertificate => _certificate ?? throw new InvalidOperationException("Native WebRTC-Direct certificate is not available.");

    private async Task DialBrowserAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        if (_jsRuntime is null)
        {
            throw new InvalidOperationException("Browser WebRTC-Direct requires Microsoft.JSInterop.IJSRuntime in the libp2p service provider.");
        }

        (IPEndPoint _, DtlsFingerprint expectedFingerprint) = WebRtcDirectMultiaddr.Parse(remoteAddr);

        await using BrowserWebRtcDirectConnection browserConnection = new(_jsRuntime);
        BrowserWebRtcDirectConnectResult result = await browserConnection.ConnectAsync(remoteAddr, token);
        DtlsFingerprint localFingerprint = DtlsFingerprint.ParseFromSdp(result.LocalFingerprint);

        byte[] prologue = WebRtcNoisePrologue.Build(localFingerprint, expectedFingerprint);
        BrowserWebRtcStreamChannel handshakeStream = new(browserConnection.HandshakeChannel);
        (IChannel _, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
            handshakeStream,
            context.Peer.Identity,
            prologue,
            isInitiator: false,
            token);

        WebRtcProtocol.ValidateRemotePublicKey(remoteAddr, remoteKey);
        PeerId remotePeerId = new(remoteKey);
        if (!remoteAddr.Has<P2P>())
        {
            remoteAddr = remoteAddr.Add<P2P>(remotePeerId.ToString());
        }

        INewConnectionContext connectionContext = context.CreateConnection();
        connectionContext.State.RemotePublicKey = remoteKey;
        connectionContext.State.RemoteAddress = remoteAddr;

        INewSessionContext session = connectionContext.UpgradeToSession();

        try
        {
            await ServeBrowserSessionAsync(browserConnection, session, session.Token);
        }
        finally
        {
            session.Dispose();
            connectionContext.Dispose();
        }
    }

    private static async Task ServeBrowserSessionAsync(
        BrowserWebRtcDirectConnection browserConnection,
        INewSessionContext session,
        CancellationToken token)
    {
        TaskCompletionSource connectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        browserConnection.HandshakeChannel.GetAwaiter().OnCompleted(() => connectionClosed.TrySetResult());

        Task outboundLoop = Task.Run(async () =>
        {
            await foreach (UpgradeOptions request in session.DialRequests.WithCancellation(token))
            {
                BrowserWebRtcDataChannel rawStream = await browserConnection.OpenStreamAsync(token);
                BrowserWebRtcStreamChannel stream = new(rawStream);
                _ = session.Upgrade(stream, request with { ModeOverride = UpgradeModeOverride.Dial });
            }
        }, token);

        Task inboundLoop = Task.Run(async () =>
        {
            await foreach (BrowserWebRtcDataChannel rawStream in browserConnection.InboundChannels.WithCancellation(token))
            {
                BrowserWebRtcStreamChannel stream = new(rawStream);
                _ = session.Upgrade(stream, new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
            }
        }, token);

        Task completed = await Task.WhenAny(outboundLoop, inboundLoop, connectionClosed.Task);
        if (completed.IsFaulted)
        {
            await completed;
        }
    }

    private async Task HandleIceLiteStunAsync(
        ITransportContext context,
        IPEndPoint localEndpoint,
        Multiaddress listenerAddr,
        UdpClient udp,
        UdpReceiveResult packet,
        ConcurrentDictionary<IPEndPoint, WebRtcDirectIceLiteConnection> connections,
        object udpSendLock,
        CancellationToken token)
    {
        if (!_offerRateLimiter.TryAccept(packet.RemoteEndPoint, packet.Buffer.Length, out string? rejectReason))
        {
            _logger?.LogDebug("Dropped WebRTC-Direct STUN packet from {Remote}: {Reason}", packet.RemoteEndPoint, rejectReason);
            return;
        }

        STUNMessage stun;
        try
        {
            stun = STUNMessage.ParseSTUNMessage(packet.Buffer, packet.Buffer.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed parsing WebRTC-Direct STUN packet from {Remote}", packet.RemoteEndPoint);
            return;
        }

        if (stun.Header.MessageType != STUNMessageTypesEnum.BindingRequest ||
            !TryGetIceCredential(packet.Buffer, out string? iceCredential))
        {
            return;
        }

        _logger?.LogDebug("Accepted WebRTC-Direct STUN binding request from {Remote}.", packet.RemoteEndPoint);

        if (!connections.TryGetValue(packet.RemoteEndPoint, out WebRtcDirectIceLiteConnection? connection) ||
            !connection.IceCredential.Equals(iceCredential, StringComparison.Ordinal))
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            IPEndPoint remoteEndpoint = packet.RemoteEndPoint;
            connection = new WebRtcDirectIceLiteConnection(
                udp,
                localEndpoint,
                remoteEndpoint,
                NativeCertificate,
                iceCredential!,
                udpSendLock,
                _logger,
                token);

            connections[remoteEndpoint] = connection;
            connection.Start(async (muxer, remoteFingerprint, connectionToken) =>
                await HandleIceLiteConnectedAsync(
                    context,
                    listenerAddr,
                    remoteEndpoint,
                    muxer,
                    remoteFingerprint,
                    connectionToken));

            _ = connection.Completion.ContinueWith(_ =>
            {
                connections.TryRemove(remoteEndpoint, out WebRtcDirectIceLiteConnection? _);
                return connection.DisposeAsync().AsTask();
            }, TaskScheduler.Default).Unwrap();
        }

        STUNMessage response = new(STUNMessageTypesEnum.BindingSuccessResponse);
        response.Header.TransactionId = stun.Header.TransactionId;
        response.AddXORMappedAddressAttribute(packet.RemoteEndPoint.Address, packet.RemoteEndPoint.Port);
        byte[] responseBytes = response.ToByteBufferStringKey(iceCredential, addFingerprint: true);

        lock (udpSendLock)
        {
            udp.Send(responseBytes, responseBytes.Length, packet.RemoteEndPoint);
        }
    }

    private async Task HandleIceLiteConnectedAsync(
        ITransportContext context,
        Multiaddress listenerAddr,
        IPEndPoint remoteEndpoint,
        WebRtcDirectSctpMuxer muxer,
        DtlsFingerprint remoteFingerprint,
        CancellationToken token)
    {
        SctpWebRtcDataChannel rawNoiseChannel = muxer.GetNegotiatedChannel(0);
        await muxer.Connected.WaitAsync(token);
        await rawNoiseChannel.Opened.WaitAsync(token);

        Multiaddress remoteAddr = remoteEndpoint.ToMultiaddress(ProtocolType.Udp);

        BrowserWebRtcStreamChannel noiseStream = new(rawNoiseChannel);
        byte[] prologue = WebRtcNoisePrologue.Build(remoteFingerprint, NativeFingerprint);
        (IChannel _, PublicKey remoteKey) = await WebRtcDirectNoiseHandshake.HandshakeAsync(
            noiseStream,
            context.Peer.Identity,
            prologue,
            isInitiator: true,
            token);

        INewConnectionContext connectionContext = context.CreateConnection();
        connectionContext.State.LocalAddress = listenerAddr;
        connectionContext.State.RemoteAddress = remoteAddr;
        connectionContext.State.RemotePublicKey = remoteKey;
        PeerId remotePeerId = new(remoteKey);
        connectionContext.State.RemoteAddress = remoteAddr.Add<P2P>(remotePeerId.ToString());

        INewSessionContext session = connectionContext.UpgradeToSession();

        try
        {
            await ServeSctpSessionAsync(muxer, session, session.Token);
        }
        finally
        {
            session.Dispose();
            connectionContext.Dispose();
        }
    }

    private static async Task ServeSctpSessionAsync(
        WebRtcDirectSctpMuxer muxer,
        INewSessionContext session,
        CancellationToken token)
    {
        Task outboundLoop = Task.Run(async () =>
        {
            await foreach (UpgradeOptions request in session.DialRequests.WithCancellation(token))
            {
                SctpWebRtcDataChannel rawStream = await muxer.OpenStreamAsync(token);
                BrowserWebRtcStreamChannel stream = new(rawStream);
                _ = session.Upgrade(stream, request with { ModeOverride = UpgradeModeOverride.Dial });
            }
        }, token);

        Task inboundLoop = Task.Run(async () =>
        {
            await foreach (SctpWebRtcDataChannel rawStream in muxer.InboundChannels.WithCancellation(token))
            {
                BrowserWebRtcStreamChannel stream = new(rawStream);
                _ = session.Upgrade(stream, new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
            }
        }, token);

        Task completed = await Task.WhenAny(outboundLoop, inboundLoop, muxer.Completed);
        if (completed.IsFaulted)
        {
            await completed;
        }
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

    private static bool IsStunPacket(ReadOnlySpan<byte> packet)
    {
        return packet.Length >= STUNHeader.STUN_HEADER_LENGTH &&
               (packet[0] & STUNHeader.STUN_INITIAL_BYTE_MASK) == 0 &&
               BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]) == STUNHeader.MAGIC_COOKIE;
    }

    private static bool TryReadPrefixedMessage(byte[] packet, string prefix, out string message)
    {
        byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);
        if (packet.AsSpan().StartsWith(prefixBytes))
        {
            message = Encoding.UTF8.GetString(packet.AsSpan(prefixBytes.Length));
            return true;
        }

        message = string.Empty;
        return false;
    }

    private static bool TryGetIceCredential(byte[] packet, out string? iceCredential)
    {
        iceCredential = null;

        STUNMessage stun = STUNMessage.ParseSTUNMessage(packet, packet.Length);
        STUNAttribute? usernameAttribute = stun.Attributes.FirstOrDefault(a => a.AttributeType == STUNAttributeTypesEnum.Username);
        if (usernameAttribute?.Value is null || usernameAttribute.Value.Length == 0)
        {
            return false;
        }

        string username = Encoding.UTF8.GetString(usernameAttribute.Value);
        IEnumerable<string> candidates = username
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat([username])
            .Where(candidate => candidate.Contains(IceCredentialPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal);

        foreach (string candidate in candidates)
        {
            STUNMessage integrityCheck = STUNMessage.ParseSTUNMessage(packet, packet.Length);
            if (integrityCheck.CheckIntegrity(Encoding.UTF8.GetBytes(candidate)))
            {
                iceCredential = candidate;
                return true;
            }
        }

        return false;
    }

}
