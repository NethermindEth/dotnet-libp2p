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
    /// Must match KademliaConfig.KSize (default 20 per libp2p spec).
    /// </summary>
    public int KValue { get; set; } = 20;

    /// <summary>
    /// Optional callback to add a newly-seen peer to the routing table.
    /// Set once the KadDhtProtocol is fully initialized so that early incoming
    /// connections can still register peers (avoids the null-closure bug).
    /// </summary>
    public Action<DhtNode>? AddNodeCallback { get; set; }

    /// <summary>
    /// Gets the distributed value store for the DHT.
    /// </summary>
    public DhtValueStore ValueStore { get; }

    /// <summary>
    /// Creates shared DHT state with optional routing table reference.
    /// </summary>
    /// <param name="routingTable">Optional routing table for efficient peer lookups. If null, GetKNearestPeers returns empty array.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="maintenanceInterval">Optional cleanup interval for value store. Defaults to 1 hour.</param>
    public SharedDhtState(IRoutingTable<ValueHash256, DhtNode>? routingTable = null, ILoggerFactory? loggerFactory = null, TimeSpan? maintenanceInterval = null)
    {
        _logger = loggerFactory?.CreateLogger<SharedDhtState>() ?? NullLogger<SharedDhtState>.Instance;
        _routingTable = routingTable;
        ValueStore = new DhtValueStore(loggerFactory, cleanupInterval: maintenanceInterval);
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
    /// Deduplicates by PeerId so multiple routing table entries for the same
    /// physical peer (e.g. different multiaddresses) don't produce duplicate RPCs.
    /// </summary>
    public DhtNode[] GetKNearestPeers(PublicKey targetKey, int k = 0)
    {
        if (k <= 0) k = KValue;

        if (_routingTable == null)
        {
            _logger.LogDebug("No routing table available");
            return Array.Empty<DhtNode>();
        }

        // O(log n) lookup using k-buckets, then deduplicate by PeerId.
        // The routing table may contain multiple entries that map to the same
        // physical peer (same PeerId but different Kademlia hashes or addresses).
        var seen = new HashSet<string>();
        var closestPeers = new List<DhtNode>(k);

        foreach (var peer in _routingTable.GetKNearestNeighbour(targetKey.Hash))
        {
            var peerIdStr = peer.PeerId?.ToString();
            if (peerIdStr != null && !seen.Add(peerIdStr))
            {
                _logger.LogDebug("Skipping duplicate peer {PeerId} in nearest-peer results", peerIdStr);
                continue;
            }

            closestPeers.Add(peer);
            if (closestPeers.Count >= k)
                break;
        }

        _logger.LogDebug("Found {Count} nearest unique peers to target using routing table", closestPeers.Count);
        return closestPeers.ToArray();
    }

    /// <summary>
    /// Get count of peers in routing table.
    /// </summary>
    public int PeerCount => _routingTable?.Size ?? 0;
}
