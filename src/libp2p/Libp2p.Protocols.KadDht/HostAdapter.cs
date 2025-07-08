using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht
{
    /// <summary>
    /// Adapter that implements IHost using ILocalPeer.
    /// </summary>
    public class HostAdapter : IHost
    {
        private readonly ILocalPeer _localPeer;

        /// <summary>
        /// Creates a new instance of HostAdapter.
        /// </summary>
        /// <param name="localPeer">The local peer to adapt.</param>
        public HostAdapter(ILocalPeer localPeer)
        {
            _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        }

        /// <summary>
        /// Gets the peer ID of the host.
        /// </summary>
        /// <returns>The peer ID of the host.</returns>
        public PeerId GetPeerId()
        {
            return _localPeer.Identity.PeerId;
        }

        /// <summary>
        /// Dials a peer with the specified peer ID.
        /// </summary>
        /// <param name="peerId">The peer ID to dial.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous dial operation.</returns>
        public Task<ISession> DialPeerAsync(PeerId peerId, CancellationToken token = default)
        {
            return _localPeer.DialAsync(peerId, token);
        }
    }
} 