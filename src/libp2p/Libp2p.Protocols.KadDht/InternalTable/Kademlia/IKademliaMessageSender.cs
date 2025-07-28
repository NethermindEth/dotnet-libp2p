// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Should be exposed by application to kademlia so that kademlia can send out message.
/// </summary>
/// <typeparam name="TNode">The node type.</typeparam>
/// <typeparam name="TKey">The key type.</typeparam>
public interface IKademliaMessageSender<TNode, TKey> where TNode : notnull
{
    /// <summary>
    /// Sends a ping message to a remote node.
    /// </summary>
    /// <param name="receiver">The receiver node.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Ping(TNode receiver, CancellationToken token);

    /// <summary>
    /// Sends a find neighbors message to a remote node.
    /// </summary>
    /// <param name="receiver">The receiver node.</param>
    /// <param name="target">The target key.</param>
    /// <param name="token">The cancellation token.</param>
    /// <returns>An array of nodes closest to the target.</returns>
    Task<TNode[]> FindNeighbours(TNode receiver, TKey target, CancellationToken token);
}


