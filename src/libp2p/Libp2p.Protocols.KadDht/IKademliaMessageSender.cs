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

using System.Threading;

namespace Libp2p.Protocols.KadDht
{
    /// <summary>
    /// Interface for sending Kademlia DHT messages to peers.
    /// </summary>
    /// <typeparam name="TPeerId">The peer ID type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    public interface IKademliaMessageSender<TPeerId, TKey>
    {
        /// <summary>
        /// Sends a ping message to a remote peer.
        /// </summary>
        /// <param name="receiver">The receiver peer.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task Ping(TPeerId receiver, CancellationToken token);

        /// <summary>
        /// Sends a find neighbors message to a remote peer.
        /// </summary>
        /// <param name="receiver">The receiver peer.</param>
        /// <param name="target">The target key.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>An array of peers closest to the target.</returns>
        Task<TKey[]> FindNeighbours(TPeerId receiver, TKey target, CancellationToken token);
    }
} 
