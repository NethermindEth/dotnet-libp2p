// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Libp2p.Protocols.KadDht.InternalTable.Crypto;


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Main find closest-k node within the network. See the kademlia paper, 2.3.
/// Since find value is basically the same also just with a shortcut, this allow changing the find neighbour op.
/// Find closest-k is also used to determine which node should store a particular value which is used by
/// store RPC (not implemented).
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface ILookupAlgo<TNode, TKey> where TNode : notnull
{
    /// <summary>
    /// The find neighbour operation here is configurable because the same algorithm is also used for finding
    /// value in the network, except that it would short circuit once the value was found.
    /// </summary>
    /// <param name="targetKey">The target key to look up.</param>
    /// <param name="k">The number of nearest neighbors to find.</param>
    /// <param name="findNeighbourOp">The operation to find neighbors.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>An array of the k nearest neighbors.</returns>
    Task<TNode[]> Lookup(
        TKey targetKey,
        int k,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    );
}

