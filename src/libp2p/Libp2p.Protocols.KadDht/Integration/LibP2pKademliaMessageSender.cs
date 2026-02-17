// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Integration;

public class LibP2pKademliaMessageSender : IDhtMessageSender
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<LibP2pKademliaMessageSender>? _logger;
    private readonly TimeSpan _operationTimeout;
    private readonly Action<DhtNode>? _onPeerDiscovered;

    public LibP2pKademliaMessageSender(
        ILocalPeer localPeer,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? operationTimeout = null,
        Action<DhtNode>? onPeerDiscovered = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<LibP2pKademliaMessageSender>();
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        _onPeerDiscovered = onPeerDiscovered;
    }

    public async Task Ping(DhtNode receiver, CancellationToken token = default)
    {
        _logger?.LogDebug("Sending Ping to {NodeId}", receiver.PeerId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreatePingRequest();
            await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            _logger?.LogTrace("Ping response from {NodeId}", receiver.PeerId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("Ping to {NodeId} cancelled", receiver.PeerId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ping to {NodeId} failed", receiver.PeerId);
        }
    }

    public async Task<DhtNode[]> FindNeighbours(DhtNode receiver, PublicKey target, CancellationToken token = default)
    {
        _logger?.LogDebug("Sending FindNeighbours to {NodeId}", receiver.PeerId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateFindNodeRequest(target.Bytes.ToArray());
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            var nodes = ParseCloserPeers(response);
            _logger?.LogDebug("Received {Count} neighbours from {NodeId}", nodes.Length, receiver.PeerId);
            return nodes;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("FindNeighbours to {NodeId} cancelled", receiver.PeerId);
            return Array.Empty<DhtNode>();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FindNeighbours to {NodeId} failed", receiver.PeerId);
            return Array.Empty<DhtNode>();
        }
    }

    public async Task<bool> PutValueAsync(DhtNode receiver, byte[] key, byte[] value, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreatePutValueRequest(key, value);
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            // Per spec: verify the echoed record matches what was sent
            if (response.Record is null)
                return false;

            if (!response.Record.Key.Span.SequenceEqual(key))
            {
                _logger?.LogWarning("PutValue to {NodeId}: echoed key mismatch", receiver.PeerId);
                return false;
            }

            if (!response.Record.Value.Span.SequenceEqual(value))
            {
                _logger?.LogWarning("PutValue to {NodeId}: echoed value mismatch", receiver.PeerId);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PutValue to {NodeId} failed", receiver.PeerId);
            return false;
        }
    }

    public async Task<GetValueResult> GetValueAsync(DhtNode receiver, byte[] key, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateGetValueRequest(key);
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            byte[]? value = null;
            long timestamp = 0;
            if (response.Record is { Value.IsEmpty: false })
            {
                value = response.Record.Value.ToByteArray();
                if (response.Record.HasTimeReceived && DateTimeOffset.TryParse(response.Record.TimeReceived, out var dto))
                    timestamp = dto.ToUnixTimeSeconds();
            }

            return new GetValueResult
            {
                Value = value,
                Timestamp = timestamp,
                CloserPeers = ParseCloserPeers(response)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetValue from {NodeId} failed", receiver.PeerId);
            return new GetValueResult();
        }
    }

    public async Task AddProviderAsync(DhtNode receiver, byte[] key, DhtNode provider, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateAddProviderRequest(key, new[] { provider });
            await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AddProvider to {NodeId} failed", receiver.PeerId);
        }
    }

    public async Task<GetProvidersResult> GetProvidersAsync(DhtNode receiver, byte[] key, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateGetProvidersRequest(key);
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            return new GetProvidersResult
            {
                Providers = ParsePeersAndStore(response.ProviderPeers),
                CloserPeers = ParseCloserPeers(response)
            };
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetProviders from {NodeId} failed", receiver.PeerId);
            return new GetProvidersResult();
        }
    }

    private DhtNode[] ParseCloserPeers(Message response)
    {
        return ParsePeersAndStore(response.CloserPeers);
    }

    private DhtNode[] ParsePeersAndStore(IReadOnlyList<Message.Types.Peer> wirePeers)
    {
        if (wirePeers.Count == 0) return Array.Empty<DhtNode>();

        var nodes = new List<DhtNode>(wirePeers.Count);
        foreach (var wp in wirePeers)
        {
            if (MessageHelper.FromWirePeer(wp) is { } node)
            {
                nodes.Add(node);
                // Per spec: persist discovered peer addresses to peerbook
                if (_onPeerDiscovered is not null && node.Multiaddrs.Count > 0)
                    _onPeerDiscovered(node);
            }
        }
        return nodes.ToArray();
    }

    private async Task<ISession> DialNodeAsync(DhtNode targetNode, CancellationToken cancellationToken)
    {
        foreach (var addrStr in targetNode.Multiaddrs)
        {
            if (string.IsNullOrWhiteSpace(addrStr)) continue;
            try
            {
                var address = Multiaddress.Decode(addrStr);
                return await _localPeer.DialAsync(address, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Failed to dial {Address} for {NodeId}", addrStr, targetNode.PeerId);
            }
        }

        try
        {
            var basicAddress = Multiaddress.Decode($"/p2p/{targetNode.PeerId}");
            return await _localPeer.DialAsync(basicAddress, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to connect to node {targetNode.PeerId}", ex);
        }
    }
}
