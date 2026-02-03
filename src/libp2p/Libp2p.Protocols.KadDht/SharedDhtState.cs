// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Shared state for DHT protocol handlers to access routing table and peer information.
/// This allows incoming protocol requests to query and update the routing table efficiently.
/// </summary>
public class SharedDhtState
{
    private readonly ILogger<SharedDhtState> _logger;
    private IRoutingTable<ValueHash256, DhtNode>? _routingTable;

    /// <summary>
    /// Gets the local peer's public key.
    /// </summary>
    public PublicKey? LocalPeerKey { get; set; }

    /// <summary>
    /// Gets the maximum number of nearest neighbors to return in FindNeighbours responses.
    /// </summary>
    public int KValue { get; set; } = 16;

    /// <summary>
    /// Gets the distributed value store for the DHT.
    /// </summary>
    public DhtValueStore ValueStore { get; }

    /// <summary>
    /// Creates shared DHT state with optional routing table reference.
    /// </summary>
    /// <param name="routingTable">Optional routing table for efficient peer lookups. If null, GetKNearestPeers returns empty array.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    public SharedDhtState(IRoutingTable<ValueHash256, DhtNode>? routingTable = null, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<SharedDhtState>() ?? NullLogger<SharedDhtState>.Instance;
        _routingTable = routingTable;
        ValueStore = new DhtValueStore(loggerFactory);
    }

    /// <summary>
    /// Updates the routing table reference. This allows late initialization of the routing table
    /// after SharedDhtState has been created and registered in DI container.
    /// </summary>
    /// <param name="routingTable">The routing table to use for peer lookups.</param>
    public void SetRoutingTable(IRoutingTable<ValueHash256, DhtNode> routingTable)
    {
        _routingTable = routingTable;
        _logger.LogInformation("Routing table updated with {PeerCount} peers", routingTable.Size);
    }

    /// <summary>
    /// Get the K nearest peers to a target key using XOR distance.
    /// </summary>
    public DhtNode[] GetKNearestPeers(PublicKey targetKey, int k = 0)
    {
        if (k <= 0) k = KValue;

        if (_routingTable == null)
        {
            _logger.LogDebug("No routing table available");
            return Array.Empty<DhtNode>();
        }

        // O(log n) lookup using k-buckets
        var closestPeers = _routingTable
            .GetKNearestNeighbour(targetKey.Hash)
            .Take(k)
            .ToArray();

        _logger.LogDebug("Found {Count} nearest peers to target using routing table", closestPeers.Length);
        return closestPeers;
    }

    /// <summary>
    /// Get count of peers in routing table.
    /// </summary>
    public int PeerCount => _routingTable?.Size ?? 0;
}
