using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht
{
    /// <summary>
    /// Interface for a libp2p host.
    /// </summary>
    public interface IHost
    {
        /// <summary>
        /// Gets the peer ID of the host.
        /// </summary>
        /// <returns>The peer ID of the host.</returns>
        PeerId GetPeerId();
        
        /// <summary>
        /// Dials a peer with the specified peer ID.
        /// </summary>
        /// <param name="peerId">The peer ID to dial.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>A task that represents the asynchronous dial operation.</returns>
        Task<ISession> DialPeerAsync(PeerId peerId, CancellationToken token = default);
    }
} 