// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Shared state for DHT protocol handlers to access routing table and peer information.
/// This allows incoming protocol requests to query and update the routing table.
/// </summary>
public class SharedDhtState
{
    private readonly ILogger<SharedDhtState> _logger;
    private readonly ConcurrentDictionary<PublicKey, DhtNode> _knownPeers = new();

    /// <summary>
    /// Gets the local peer's public key.
    /// </summary>
    public PublicKey? LocalPeerKey { get; set; }

    /// <summary>
    /// Gets the maximum number of nearest neighbors to return in FindNeighbours responses.
    /// </summary>
    public int KValue { get; set; } = 16;

    public SharedDhtState(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<SharedDhtState>() ?? NullLogger<SharedDhtState>.Instance;
    }

    /// <summary>
    /// Add or update a known peer in the shared state.
    /// </summary>
    public void AddOrUpdatePeer(PublicKey key, DhtNode node)
    {
        _knownPeers[key] = node;
        _logger.LogTrace("Added/updated peer {PeerId} in shared state", key);
    }

    /// <summary>
    /// Get a peer by public key.
    /// </summary>
    public DhtNode? GetPeer(PublicKey key)
    {
        return _knownPeers.TryGetValue(key, out var node) ? node : null;
    }

    /// <summary>
    /// Get the K nearest peers to a target key using XOR distance.
    /// </summary>
    public DhtNode[] GetKNearestPeers(PublicKey targetKey, int k = 0)
    {
        if (k <= 0) k = KValue;

        if (_knownPeers.IsEmpty)
        {
            _logger.LogDebug("No known peers in routing table");
            return Array.Empty<DhtNode>();
        }

        var targetHash = targetKey.Hash;

        // Calculate XOR distance to all known peers and sort by distance (smaller ValueHash256 means closer)
        var sortedPeers = _knownPeers
            .Select(kvp => new
            {
                Node = kvp.Value,
                Distance = ValueHash256.XorDistance(kvp.Key.Hash, targetHash)
            })
            .OrderBy(x => x.Distance)
            .Take(k)
            .Select(x => x.Node)
            .ToArray();

        _logger.LogDebug("Found {Count} nearest peers to target {Target}", sortedPeers.Length, targetKey);
        return sortedPeers;
    }

    /// <summary>
    /// Get all known peers.
    /// </summary>
    public DhtNode[] GetAllPeers()
    {
        return _knownPeers.Values.ToArray();
    }

    /// <summary>
    /// Get count of known peers.
    /// </summary>
    public int PeerCount => _knownPeers.Count;

    /// <summary>
    /// Remove a peer from shared state.
    /// </summary>
    public bool RemovePeer(PublicKey key)
    {
        var removed = _knownPeers.TryRemove(key, out _);
        if (removed)
        {
            _logger.LogTrace("Removed peer {PeerId} from shared state", key);
        }
        return removed;
    }
}
