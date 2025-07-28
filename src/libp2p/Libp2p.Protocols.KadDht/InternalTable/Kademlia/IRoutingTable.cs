// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Interface for a routing table in Kademlia DHT.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IRoutingTable<TNode, TKey> where TNode : notnull
{
    /// <summary>
    /// Tries to add or refresh a node in the routing table.
    /// </summary>
    /// <param name="key">The key of the node.</param>
    /// <param name="item">The node to add or refresh.</param>
    /// <param name="toRefresh">The node that needs to be refreshed, if any.</param>
    /// <returns>The result of the operation.</returns>
    BucketAddResult TryAddOrRefresh(in TKey key, TNode item, out TNode? toRefresh);

    /// <summary>
    /// Removes a node from the routing table.
    /// </summary>
    /// <param name="key">The key of the node to remove.</param>
    /// <returns>True if the node was removed, false otherwise.</returns>
    bool Remove(in TKey key);

    /// <summary>
    /// Gets the k nearest neighbors to a key.
    /// </summary>
    /// <param name="key">The target key.</param>
    /// <param name="exclude">The key to exclude.</param>
    /// <param name="excludeSelf">Whether to exclude the current node.</param>
    /// <returns>An array of the k nearest neighbors.</returns>
    TNode[] GetKNearestNeighbour(TKey key, TKey? exclude = default, bool excludeSelf = false);

    /// <summary>
    /// Gets all nodes at a specific distance.
    /// </summary>
    /// <param name="i">The distance.</param>
    /// <returns>An array of nodes at the specified distance.</returns>
    TNode[] GetAllAtDistance(int i);

    /// <summary>
    /// Iterates over all buckets in the routing table.
    /// </summary>
    /// <returns>An enumerable of bucket information.</returns>
    IEnumerable<(TKey Prefix, int Distance, KBucket<TNode> Bucket)> IterateBuckets();

    /// <summary>
    /// Gets a node by its key.
    /// </summary>
    /// <param name="key">The key of the node.</param>
    /// <returns>The node if found, null otherwise.</returns>
    TNode? GetByKey(TKey key);

    /// <summary>
    /// Logs debug information about the routing table.
    /// </summary>
    void LogDebugInfo();

    /// <summary>
    /// Event raised when a node is added to the routing table.
    /// </summary>
    event EventHandler<TNode>? OnNodeAdded;

    /// <summary>
    /// Gets the total number of nodes in the routing table.
    /// </summary>
    int Size { get; }
}

