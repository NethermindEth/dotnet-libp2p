// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Libp2p Kademlia message sender using the spec-compliant unified Message envelope.
/// Uses the single /ipfs/kad/1.0.0 protocol for all DHT operations.
/// </summary>
public class LibP2pKademliaMessageSender : Kademlia.IKademliaMessageSender<PublicKey, DhtNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<LibP2pKademliaMessageSender>? _logger;
    private readonly TimeSpan _operationTimeout;

    public LibP2pKademliaMessageSender(
        ILocalPeer localPeer,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? operationTimeout = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<LibP2pKademliaMessageSender>();
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
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

            var nodes = response.CloserPeers
                .Select(MessageHelper.FromWirePeer)
                .Where(n => n != null)
                .Cast<DhtNode>()
                .ToArray();

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

    private async Task<ISession> DialNodeAsync(DhtNode targetNode, CancellationToken cancellationToken)
    {
        // Try known multiaddresses from the node
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

        // Fallback to peer ID only (for mDNS-discovered peers)
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

    public void Dispose()
    {
        _logger?.LogDebug("LibP2p Kademlia message sender disposed");
    }
}
