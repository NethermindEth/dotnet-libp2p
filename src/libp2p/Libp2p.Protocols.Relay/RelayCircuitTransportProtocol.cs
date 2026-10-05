// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Relay.Dto;

namespace Nethermind.Libp2p.Protocols;

public class RelayCircuitTransportProtocol(ILoggerFactory? loggerFactory = null) : ITransportProtocol
{
    private readonly ILogger<RelayCircuitTransportProtocol>? _logger = loggerFactory?.CreateLogger<RelayCircuitTransportProtocol>();

    public string Id => "p2p-circuit";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];

    public static bool IsAddressMatch(Multiaddress addr) => RelayCircuitMultiaddr.IsCircuit(addr);

    public Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        context.ListenerReady(listenAddr);
        return Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        RelayCircuitAddress circuitAddress = RelayCircuitMultiaddr.Parse(remoteAddr);
        _logger?.LogDebug("Dialling relay circuit address {Address} via relay {RelayAddress}", remoteAddr, circuitAddress.RelayAddress);

        ISession relaySession = await context.Peer.DialAsync(circuitAddress.RelayAddress, token).ConfigureAwait(false);
        IChannel hopChannel = await relaySession.OpenStreamAsync<RelayHopProtocol>(token).ConfigureAwait(false);

        var connectRequest = new HopMessage
        {
            Type = HopMessage.Types.Type.Connect,
            Peer = new Peer
            {
                Id = ByteString.CopyFrom(circuitAddress.TargetPeer.Bytes)
            }
        };

        await hopChannel.WriteSizeAndProtobufAsync(connectRequest).ConfigureAwait(false);
        HopMessage connectResponse = await hopChannel.ReadPrefixedProtobufAsync(HopMessage.Parser, token).ConfigureAwait(false);
        if (connectResponse.Status != Status.Ok)
        {
            await hopChannel.CloseAsync().ConfigureAwait(false);
            throw new Libp2pException($"Relay CONNECT failed with status {connectResponse.Status}.");
        }

        INewConnectionContext relayedConnection = context.CreateConnection();
        relayedConnection.State.RemoteAddress = circuitAddress.CircuitAddress;

        try
        {
            await relayedConnection.Upgrade(hopChannel).ConfigureAwait(false);
        }
        finally
        {
            relayedConnection.Dispose();
        }
    }
}

internal sealed record RelayCircuitAddress(Multiaddress RelayAddress, Multiaddress CircuitAddress, PeerId TargetPeer);

internal static class RelayCircuitMultiaddr
{
    public static bool IsCircuit(Multiaddress addr)
    {
        string[] segments = GetSegments(addr);
        int circuitIndex = GetCircuitIndex(segments);
        return circuitIndex >= 0 && !HasRelayedWebRtcMarker(segments, circuitIndex);
    }

    public static RelayCircuitAddress Parse(Multiaddress addr)
    {
        string[] segments = GetSegments(addr);
        int circuitIndex = GetCircuitIndex(segments);
        if (circuitIndex <= 0)
        {
            throw new FormatException($"Expected relay address followed by /p2p-circuit in multiaddr: {addr}");
        }

        int targetP2pIndex = -1;
        for (int i = segments.Length - 2; i > circuitIndex; i--)
        {
            if (segments[i].Equals("p2p", StringComparison.OrdinalIgnoreCase))
            {
                targetP2pIndex = i;
                break;
            }
        }

        if (targetP2pIndex < 0)
        {
            throw new FormatException($"Expected target /p2p component after /p2p-circuit in multiaddr: {addr}");
        }

        string relayAddressText = "/" + string.Join('/', segments.Take(circuitIndex));

        return new RelayCircuitAddress(
            Multiaddress.Decode(relayAddressText),
            ToCircuitAddress(addr),
            new PeerId(segments[targetP2pIndex + 1]));
    }

    public static Multiaddress ToCircuitAddress(Multiaddress addr)
    {
        List<string> segments = [.. GetSegments(addr)];
        int circuitIndex = GetCircuitIndex([.. segments]);
        if (circuitIndex < 0)
        {
            throw new FormatException($"Expected /p2p-circuit in multiaddr: {addr}");
        }

        if (HasRelayedWebRtcMarker([.. segments], circuitIndex))
        {
            segments.RemoveAt(circuitIndex + 1);
        }

        return Multiaddress.Decode("/" + string.Join('/', segments));
    }

    private static string[] GetSegments(Multiaddress addr)
        => addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static int GetCircuitIndex(string[] segments)
        => Array.FindIndex(segments, p => p.Equals("p2p-circuit", StringComparison.OrdinalIgnoreCase));

    private static bool HasRelayedWebRtcMarker(string[] segments, int circuitIndex)
        => circuitIndex + 1 < segments.Length &&
           segments[circuitIndex + 1].Equals("webrtc", StringComparison.OrdinalIgnoreCase);
}
