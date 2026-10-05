// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Protocols.WebRtc;

public class WebRtcProtocol(ILoggerFactory? loggerFactory = null, IJSRuntime? jsRuntime = null) : ITransportProtocol
{
    private readonly ILogger<WebRtcProtocol>? _logger = loggerFactory?.CreateLogger<WebRtcProtocol>();

    public string Id => "webrtc";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [Multiaddress.Decode($"/webrtc/p2p/{peerId}")];

    public static bool IsAddressMatch(Multiaddress addr) => WebRtcMultiaddr.IsWebRtc(addr);

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        if (!listenAddr.HasPeerIdText())
        {
            listenAddr = Multiaddress.Decode($"{listenAddr}/p2p/{context.Peer.Identity.PeerId}");
        }

        context.ListenerReady(listenAddr);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("The relayed WebRTC transport currently uses the browser RTCPeerConnection API.");
        }

        if (jsRuntime is null)
        {
            throw new InvalidOperationException("Browser WebRTC requires Microsoft.JSInterop.IJSRuntime in the libp2p service provider.");
        }

        ISession? relaySession = null;
        IChannel? signaling = null;
        PublicKey? remotePublicKey = null;
        await using BrowserWebRtcRelayConnection browserConnection = new(jsRuntime);
        using CancellationTokenSource signalingCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? candidateWriter = null;
        Task? remoteCandidates = null;

        try
        {
            Multiaddress circuitAddr = WebRtcMultiaddr.ToCircuitAddress(remoteAddr);
            relaySession = await context.Peer.DialAsync(circuitAddr, token).ConfigureAwait(false);
            signaling = await relaySession.OpenStreamAsync<WebRtcSignalingProtocol>(token).ConfigureAwait(false);
            remotePublicKey = relaySession.RemotePublicKey ??
                throw new Libp2pSetupException("Relayed WebRTC requires an authenticated relay circuit session.");
            ValidateRemotePublicKey(remoteAddr, remotePublicKey);

            string offerSdp = await browserConnection.CreateOfferAsync(token).ConfigureAwait(false);
            candidateWriter = ForwardLocalIceCandidatesAsync(browserConnection, signaling, signalingCts.Token);
            await WriteSignalingAsync(signaling, WebRtcSignalingMessageType.SdpOffer, offerSdp).ConfigureAwait(false);

            WebRtcSignalingMessage answer = await WebRtcSignalingCodec.ReadAsync(signaling, token).ConfigureAwait(false);
            if (answer.Type != WebRtcSignalingMessageType.SdpAnswer)
            {
                throw new Libp2pException($"Expected WebRTC SDP answer, received {answer.Type}.");
            }

            await browserConnection.SetRemoteAnswerAsync(answer.Data, token).ConfigureAwait(false);
            Task connected = browserConnection.WaitConnectedAsync(token).AsTask();
            remoteCandidates = ReadRemoteIceCandidatesUntilConnectedAsync(browserConnection, signaling, connected, signalingCts.Token);
            await connected.ConfigureAwait(false);
            await browserConnection.CloseInitChannelAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await signalingCts.CancelAsync().ConfigureAwait(false);
            if (signaling is not null)
            {
                await CloseChannelQuietlyAsync(signaling).ConfigureAwait(false);
            }

            await ObserveSignalingTasksAsync([candidateWriter, remoteCandidates]).ConfigureAwait(false);
        }

        INewConnectionContext connectionContext = context.CreateConnection<WebRtcProtocol>();
        connectionContext.State.RemoteAddress = remoteAddr;
        connectionContext.State.RemotePublicKey = remotePublicKey ??
            throw new Libp2pSetupException("Relayed WebRTC requires an authenticated relay circuit session.");
        INewSessionContext session = connectionContext.UpgradeToSession();

        try
        {
            await ServeBrowserSessionAsync(browserConnection, session, session.Token).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            connectionContext.Dispose();
        }
    }

    internal static async Task ServeBrowserSessionAsync(
        BrowserWebRtcRelayConnection browserConnection,
        INewSessionContext session,
        CancellationToken token)
    {
        Task outboundLoop = Task.Run(async () =>
        {
            await foreach (UpgradeOptions request in session.DialRequests.WithCancellation(token))
            {
                BrowserWebRtcDataChannel rawStream = await browserConnection.OpenStreamAsync(token).ConfigureAwait(false);
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

        Task completed = await Task.WhenAny(outboundLoop, inboundLoop).ConfigureAwait(false);
        if (completed.IsFaulted)
        {
            await completed.ConfigureAwait(false);
        }
    }

    internal static async Task WriteSignalingAsync(
        IChannel channel,
        WebRtcSignalingMessageType type,
        string data)
    {
        await channel.WriteSizeAndDataAsync(WebRtcSignalingCodec.Encode(type, data)).ConfigureAwait(false);
    }

    internal static async Task ForwardLocalIceCandidatesAsync(
        BrowserWebRtcRelayConnection browserConnection,
        IChannel signaling,
        CancellationToken token)
    {
        try
        {
            await foreach (string candidate in browserConnection.LocalIceCandidates.WithCancellation(token).ConfigureAwait(false))
            {
                await WriteSignalingAsync(signaling, WebRtcSignalingMessageType.IceCandidate, candidate).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    internal static async Task ReadRemoteIceCandidatesUntilConnectedAsync(
        BrowserWebRtcRelayConnection browserConnection,
        IChannel signaling,
        Task connected,
        CancellationToken token)
    {
        while (!connected.IsCompleted)
        {
            Task<WebRtcSignalingMessage> readTask = WebRtcSignalingCodec.ReadAsync(signaling, token);
            Task completed = await Task.WhenAny(readTask, connected).ConfigureAwait(false);
            if (completed == connected)
            {
                await ObserveSignalingTasksAsync(readTask).ConfigureAwait(false);
                break;
            }

            WebRtcSignalingMessage message = await readTask.ConfigureAwait(false);
            if (message.Type == WebRtcSignalingMessageType.IceCandidate && !string.IsNullOrWhiteSpace(message.Data))
            {
                await browserConnection.AddIceCandidateAsync(message.Data, token).ConfigureAwait(false);
            }
        }
    }

    internal static async Task ObserveSignalingTasksAsync(params Task?[] tasks)
    {
        foreach (Task? task in tasks)
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    internal static async Task CloseChannelQuietlyAsync(IChannel channel)
    {
        try
        {
            await channel.CloseAsync().ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static void ValidateRemotePublicKey(Multiaddress remoteAddr, PublicKey remotePublicKey)
    {
        PeerId actualPeerId = new(remotePublicKey);
        PeerId? expectedPeerId = remoteAddr.GetPeerId();
        if (expectedPeerId is not null && expectedPeerId != actualPeerId)
        {
            throw new Libp2pException($"WebRTC remote peer id {actualPeerId} does not match address peer id {expectedPeerId}.");
        }
    }
}

public class WebRtcSignalingProtocol(ILoggerFactory? loggerFactory = null, IJSRuntime? jsRuntime = null) : ISessionListenerProtocol
{
    private readonly ILogger<WebRtcSignalingProtocol>? _logger = loggerFactory?.CreateLogger<WebRtcSignalingProtocol>();

    public string Id => "/webrtc-signaling/0.0.1";

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        if (!OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException("The relayed WebRTC signaling protocol currently uses the browser RTCPeerConnection API.");
        }

        if (jsRuntime is null)
        {
            throw new InvalidOperationException("Browser WebRTC requires Microsoft.JSInterop.IJSRuntime in the libp2p service provider.");
        }

        PeerId remotePeerId = context.State.RemotePeerId ??
            throw new Libp2pSetupException("Relayed WebRTC signaling requires an authenticated relayed session.");
        PublicKey remotePublicKey = context.State.RemotePublicKey ??
            throw new Libp2pSetupException("Relayed WebRTC signaling requires an authenticated relayed session.");
        WebRtcProtocol.ValidateRemotePublicKey(Multiaddress.Decode($"/p2p/{remotePeerId}"), remotePublicKey);

        await using BrowserWebRtcRelayConnection browserConnection = new(jsRuntime);
        using CancellationTokenSource signalingCts = CancellationTokenSource.CreateLinkedTokenSource(downChannel.CancellationToken);
        Task? candidateWriter = null;
        Task? remoteCandidates = null;

        try
        {
            await browserConnection.CreateAnswererAsync(signalingCts.Token).ConfigureAwait(false);

            WebRtcSignalingMessage offer = await WebRtcSignalingCodec.ReadAsync(downChannel, signalingCts.Token).ConfigureAwait(false);
            if (offer.Type != WebRtcSignalingMessageType.SdpOffer)
            {
                throw new Libp2pException($"Expected WebRTC SDP offer, received {offer.Type}.");
            }

            string answerSdp = await browserConnection.AcceptOfferAsync(offer.Data, signalingCts.Token).ConfigureAwait(false);
            candidateWriter = WebRtcProtocol.ForwardLocalIceCandidatesAsync(browserConnection, downChannel, signalingCts.Token);
            await WebRtcProtocol.WriteSignalingAsync(downChannel, WebRtcSignalingMessageType.SdpAnswer, answerSdp).ConfigureAwait(false);

            Task connected = browserConnection.WaitConnectedAsync(signalingCts.Token).AsTask();
            remoteCandidates = WebRtcProtocol.ReadRemoteIceCandidatesUntilConnectedAsync(browserConnection, downChannel, connected, signalingCts.Token);
            await connected.ConfigureAwait(false);
        }
        catch
        {
            throw;
        }
        finally
        {
            await signalingCts.CancelAsync().ConfigureAwait(false);
            await WebRtcProtocol.CloseChannelQuietlyAsync(downChannel).ConfigureAwait(false);
            await WebRtcProtocol.ObserveSignalingTasksAsync(candidateWriter, remoteCandidates).ConfigureAwait(false);
        }

        _logger?.LogDebug("Relayed WebRTC signaling completed for {RemotePeerId}", remotePeerId);

        INewConnectionContext connectionContext = context.CreateConnection<WebRtcProtocol>();
        connectionContext.State.RemoteAddress = Multiaddress.Decode($"/webrtc/p2p/{remotePeerId}");
        connectionContext.State.RemotePublicKey = remotePublicKey;
        INewSessionContext session = connectionContext.UpgradeToSession();

        try
        {
            await WebRtcProtocol.ServeBrowserSessionAsync(browserConnection, session, session.Token).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
            connectionContext.Dispose();
        }
    }
}

internal static class WebRtcMultiaddr
{
    public static bool IsWebRtc(Multiaddress addr)
    {
        string[] segments = GetSegments(addr);
        int circuitIndex = GetCircuitIndex(segments);
        if (circuitIndex >= 0)
        {
            return circuitIndex + 1 < segments.Length &&
                   segments[circuitIndex + 1].Equals("webrtc", StringComparison.OrdinalIgnoreCase);
        }

        return segments.Contains("webrtc", StringComparer.OrdinalIgnoreCase) &&
               !segments.Contains("webrtc-direct", StringComparer.OrdinalIgnoreCase);
    }

    public static Multiaddress ToCircuitAddress(Multiaddress addr)
    {
        List<string> segments = [.. GetSegments(addr)];
        int circuitIndex = GetCircuitIndex([.. segments]);
        if (circuitIndex < 0 ||
            circuitIndex + 1 >= segments.Count ||
            !segments[circuitIndex + 1].Equals("webrtc", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Expected /p2p-circuit/webrtc/p2p/... in multiaddr: {addr}");
        }

        segments.RemoveAt(circuitIndex + 1);
        return Multiaddress.Decode("/" + string.Join('/', segments));
    }

    public static bool HasPeerIdText(this Multiaddress addr)
    {
        string[] segments = addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("p2p", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] GetSegments(Multiaddress addr)
        => addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static int GetCircuitIndex(string[] segments)
        => Array.FindIndex(segments, p => p.Equals("p2p-circuit", StringComparison.OrdinalIgnoreCase));
}

internal enum WebRtcSignalingMessageType
{
    SdpOffer = 0,
    SdpAnswer = 1,
    IceCandidate = 2,
}

internal sealed record WebRtcSignalingMessage(WebRtcSignalingMessageType Type, string Data);

internal static class WebRtcSignalingCodec
{
    internal const int MaxMessageSize = 1024 * 1024;

    public static byte[] Encode(WebRtcSignalingMessageType type, string data)
    {
        byte[] dataBytes = Encoding.UTF8.GetBytes(data);
        int length = 1 + VarInt.GetSizeInBytes((int)type) + 1 + VarInt.GetSizeInBytes(dataBytes.Length) + dataBytes.Length;
        if (length > MaxMessageSize)
        {
            throw new FormatException($"WebRTC signaling messages cannot exceed {MaxMessageSize} bytes.");
        }

        byte[] buffer = new byte[length];
        int offset = 0;

        buffer[offset++] = 0x08;
        VarInt.Encode((int)type, buffer, ref offset);
        buffer[offset++] = 0x12;
        VarInt.Encode(dataBytes.Length, buffer, ref offset);
        dataBytes.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    public static async Task<WebRtcSignalingMessage> ReadAsync(IChannel channel, CancellationToken token = default)
    {
        int messageLength = await ReadBoundedMessageLengthAsync(channel, token).ConfigureAwait(false);
        ReadOnlySequence<byte> serializedMessage = await channel.ReadAsync(messageLength, token: token).OrThrow().ConfigureAwait(false);
        return Decode(serializedMessage.ToArray());
    }

    private static WebRtcSignalingMessage Decode(ReadOnlySpan<byte> data)
    {
        WebRtcSignalingMessageType type = WebRtcSignalingMessageType.SdpOffer;
        string messageData = string.Empty;
        int offset = 0;

        while (offset < data.Length)
        {
            ulong keyValue = ReadVarint(data, ref offset);
            if (keyValue > int.MaxValue)
            {
                throw new FormatException("WebRTC signaling protobuf field key is too large.");
            }

            int key = (int)keyValue;
            int field = key >> 3;
            int wireType = key & 0x7;

            if (field == 1 && wireType == 0)
            {
                ulong typeValue = ReadVarint(data, ref offset);
                if (typeValue > int.MaxValue)
                {
                    throw new FormatException("WebRTC signaling message type is too large.");
                }

                type = (WebRtcSignalingMessageType)typeValue;
                continue;
            }

            if (field == 2 && wireType == 2)
            {
                ulong length = ReadVarint(data, ref offset);
                if (length > (ulong)(data.Length - offset))
                {
                    throw new FormatException("Malformed WebRTC signaling message data field.");
                }

                int dataLength = (int)length;
                messageData = Encoding.UTF8.GetString(data.Slice(offset, dataLength));
                offset += dataLength;
                continue;
            }

            SkipUnknown(data, wireType, ref offset);
        }

        return new WebRtcSignalingMessage(type, messageData);
    }

    private static async Task<int> ReadBoundedMessageLengthAsync(IChannel channel, CancellationToken token)
    {
        ulong result = 0;
        int shift = 0;

        for (int i = 0; i < 10; i++)
        {
            byte value = (await channel.ReadAsync(1, token: token).OrThrow().ConfigureAwait(false)).FirstSpan[0];
            result |= ((ulong)(value & 0x7f)) << shift;
            if ((value & 0x80) == 0)
            {
                if (result > MaxMessageSize)
                {
                    throw new FormatException($"WebRTC signaling messages cannot exceed {MaxMessageSize} bytes.");
                }

                return (int)result;
            }

            shift += 7;
        }

        throw new FormatException("Invalid WebRTC signaling message length varint.");
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong result = 0;
        int shift = 0;

        while (offset < source.Length)
        {
            byte value = source[offset++];
            result |= ((ulong)(value & 0x7f)) << shift;
            if ((value & 0x80) == 0)
            {
                return result;
            }

            shift += 7;
            if (shift >= 70)
            {
                throw new FormatException("Invalid varint.");
            }
        }

        throw new FormatException("Truncated varint.");
    }

    private static void SkipUnknown(ReadOnlySpan<byte> source, int wireType, ref int offset)
    {
        switch (wireType)
        {
            case 0:
                _ = ReadVarint(source, ref offset);
                return;
            case 2:
                ulong length = ReadVarint(source, ref offset);
                if (length > (ulong)(source.Length - offset))
                {
                    throw new FormatException("Malformed length-delimited field.");
                }

                offset += (int)length;
                return;
            default:
                throw new FormatException($"Unsupported WebRTC signaling wire type: {wireType}.");
        }
    }
}
