using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;

using Libp2p.Protocols.KadDht.InternalTable.Crypto;

namespace Libp2p.Protocols.KadDht
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
    }
}

