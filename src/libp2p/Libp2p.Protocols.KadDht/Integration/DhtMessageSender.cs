// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Implementation of IKademliaMessageSender that uses libp2p protocols for network communication.
/// Bridges between Kademlia algorithm and libp2p transport layer.
/// </summary>
public sealed class DhtMessageSender : IKademliaMessageSender<PublicKey, DhtNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<DhtMessageSender>? _logger;
    private readonly TimeSpan _operationTimeout;

    public DhtMessageSender(ILocalPeer localPeer, ILoggerFactory? loggerFactory = null, TimeSpan? operationTimeout = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<DhtMessageSender>();
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task Ping(DhtNode receiver, CancellationToken token)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));

        try
        {
            _logger?.LogTrace("Sending ping to {PeerId}", receiver.PeerId);

            // Get session to the remote peer
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_operationTimeout);

            // For now, we'll use a simple connection test
            // In a full implementation, this would dial the ping sub-protocol
            var session = await _localPeer.DialAsync(receiver.PeerId, cts.Token);

            // Dispose the session immediately - ping is just a connectivity test
            await session.DisconnectAsync();

            _logger?.LogTrace("Ping to {PeerId} successful", receiver.PeerId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogTrace("Ping to {PeerId} cancelled", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogTrace("Ping to {PeerId} failed: {Error}", receiver.PeerId, ex.Message);
            throw new InvalidOperationException($"Failed to ping {receiver.PeerId}", ex);
        }
    }

    public async Task<DhtNode[]> FindNeighbours(DhtNode receiver, PublicKey target, CancellationToken token)
    {
        if (receiver == null) throw new ArgumentNullException(nameof(receiver));
        if (target == null) throw new ArgumentNullException(nameof(target));

        try
        {
            _logger?.LogTrace("Sending FindNeighbours to {PeerId} for target {TargetHash}",
                receiver.PeerId, Convert.ToHexString(target.Bytes));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(_operationTimeout);

            // Get session to the remote peer
            var session = await _localPeer.DialAsync(receiver.PeerId, cts.Token);

            try
            {
                // Create the request
                var request = new FindNeighboursRequest
                {
                    Target = new PublicKeyBytes
                    {
                        Value = ByteString.CopyFrom(target.Bytes)
                    }
                };

                // Send the request via session DialAsync
                var response = await session.DialAsync<RequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>, FindNeighboursRequest, FindNeighboursResponse>(
                    request, cts.Token);

                // Convert response to DhtNode array
                var neighbours = new List<DhtNode>();
                foreach (var node in response.Neighbours)
                {
                    try
                    {
                        var publicKey = new PublicKey(node.PublicKey.ToByteArray());
                        var peerId = new PeerId(node.PublicKey.ToByteArray()); // Simplified - real implementation would derive PeerId properly
                        var dhtNode = new DhtNode
                        {
                            PeerId = peerId,
                            PublicKey = publicKey,
                            Multiaddrs = node.Multiaddrs.ToList()
                        };
                        neighbours.Add(dhtNode);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("Failed to parse neighbour node: {Error}", ex.Message);
                        continue; // Skip malformed nodes
                    }
                }

                _logger?.LogTrace("FindNeighbours to {PeerId} returned {Count} neighbours",
                    receiver.PeerId, neighbours.Count);

                return neighbours.ToArray();
            }
            finally
            {
                await session.DisconnectAsync();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogTrace("FindNeighbours to {PeerId} cancelled", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogTrace("FindNeighbours to {PeerId} failed: {Error}", receiver.PeerId, ex.Message);
            throw new InvalidOperationException($"Failed to find neighbours from {receiver.PeerId}", ex);
        }
    }
}
