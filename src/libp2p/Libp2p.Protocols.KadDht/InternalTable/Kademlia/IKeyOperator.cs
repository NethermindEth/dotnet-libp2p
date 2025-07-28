// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    /// <summary>
    /// Interface for key operations in Kademlia DHT.
    /// </summary>
    /// <typeparam name="TNode">The node type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    public interface IKeyOperator<TNode, TKey>
    {
        /// <summary>
        /// Gets the distance between two nodes.
        /// </summary>
        /// <param name="a">The first node.</param>
        /// <param name="b">The second node.</param>
        /// <returns>The distance between the two nodes.</returns>
        int GetDistance(TNode a, TNode b);

        /// <summary>
        /// Converts a node to a key.
        /// </summary>
        /// <param name="node">The node to convert.</param>
        /// <returns>The key.</returns>
        TKey GetKey(TNode node);

        /// <summary>
        /// Gets a key representation of the byte array.
        /// </summary>
        /// <param name="key">The byte array to convert to a key.</param>
        /// <returns>A key representation of the byte array.</returns>
        TKey GetKeyHash(byte[] key);

        /// <summary>
        /// Gets a key representation of a node.
        /// </summary>
        /// <param name="node">The node to convert to a key.</param>
        /// <returns>A key representation of the node.</returns>
        TKey GetNodeHash(TNode node);

        /// <summary>
        /// Creates a random key at a specific distance from a node prefix.
        /// </summary>
        /// <param name="nodePrefix">The node prefix to use as a base.</param>
        /// <param name="depth">The distance from the node prefix.</param>
        /// <returns>A random key at the specified distance.</returns>
        byte[] CreateRandomKeyAtDistance(TKey nodePrefix, int depth);

        int GetDistance<TKey>(TKey k1, TKey k2);
        TKey GetKeyFromBytes<TKey>(byte[] keyBytes);
    }
}

