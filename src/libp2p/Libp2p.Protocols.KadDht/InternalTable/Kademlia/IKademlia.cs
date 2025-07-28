// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Main kademlia interface. High level code is expected to interface with this interface.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IKademlia<TNode, TKey> where TNode : notnull
{
    /// <summary>
    /// Add node to the table.
    /// </summary>
    /// <param name="node">The node to add.</param>
    void AddOrRefresh(TNode node);

    /// <summary>
    /// Remove from to the table.
    /// </summary>
    /// <param name="node">The node to remove.</param>
    void Remove(TNode node);

    /// <summary>
    /// Start timers, refresh and such for maintenance of the table.
    /// </summary>
    /// <param name="token">The cancellation token.</param>
    Task Run(CancellationToken token);

    /// <summary>
    /// Just do the bootstrap sequence, which is to initiate a lookup on current node id.
    /// Also do a refresh on all bucket which is not part of joining strictly speaking.
    /// </summary>
    /// <param name="token">The cancellation token.</param>
    Task Bootstrap(CancellationToken token);

    /// <summary>
    /// Lookup k nearest neighbour closest to the target key. This will traverse the network.
    /// </summary>
    /// <param name="key">The target key.</param>
    /// <param name="token">The cancellation token.</param>
    /// <param name="b"></param>
    /// <param name="k">The number of nearest neighbors to find.</param>
    Task<TNode[]> LookupNodesClosest(TKey key, CancellationToken? token, bool b, int? k = null);

    /// <summary>
    /// Return the K nearest table entry from target. This does not traverse the network. The returned array is not
    /// sorted. The routing table may return the exact same array for optimization purpose.
    /// </summary>
    /// <param name="target">The target key.</param>
    /// <param name="excluding">The node to exclude.</param>
    /// <param name="excludeSelf">Whether to exclude the current node.</param>
    TNode[] GetKNeighbour(TKey target, TNode? excluding = default, bool excludeSelf = false);

    /// <summary>
    /// Called when a node is added to the routing table.
    /// </summary>
    event EventHandler<TNode> OnNodeAdded;

    /// <summary>
    /// Iterate all nodes with no ordering.
    /// </summary>
    /// <returns>An enumerable of all nodes.</returns>
    IEnumerable<TNode> IterateNodes();
}

