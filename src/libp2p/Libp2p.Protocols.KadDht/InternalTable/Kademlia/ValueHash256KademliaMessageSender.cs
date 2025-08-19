using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using System.Collections.Concurrent;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class ValueHash256KademliaMessageSender : IKademliaMessageSender<ValueHash256, ValueHash256>
    {
        private readonly KadDhtProtocol _kadDhtProtocol;
        private readonly ILogger<ValueHash256KademliaMessageSender> _logger;
        private readonly PeerIdKeyOperator _peerIdKeyOperator;
        // Keep a cache of ValueHash256 to PeerId mappings
        private readonly ConcurrentDictionary<ValueHash256, PeerId> _peerIdCache;

        public ValueHash256KademliaMessageSender(KadDhtProtocol kadDhtProtocol, ILogger<ValueHash256KademliaMessageSender> logger, PeerIdKeyOperator peerIdKeyOperator)
        {
            _kadDhtProtocol = kadDhtProtocol ?? throw new ArgumentNullException(nameof(kadDhtProtocol));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _peerIdKeyOperator = peerIdKeyOperator ?? throw new ArgumentNullException(nameof(peerIdKeyOperator));
            _peerIdCache = new ConcurrentDictionary<ValueHash256, PeerId>();
        }

        public async Task Ping(ValueHash256 receiver, CancellationToken token)
        {
            try
            {
                // Convert ValueHash256 to PeerId
                PeerId peerId = FindPeerId(receiver);
                await _kadDhtProtocol.Ping(peerId, token);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error pinging peer {Receiver}", receiver);
                throw;
            }
        }

        public async Task<ValueHash256[]> FindNeighbours(ValueHash256 receiver, ValueHash256 target, CancellationToken token)
        {
             try
            {
                // Convert ValueHash256 to PeerId
                PeerId peerId = FindPeerId(receiver);
                var result = await _kadDhtProtocol.FindNeighbours(peerId, target, token);
                return result;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error finding neighbors for peer {Receiver}", receiver);
                throw;
            }
        }

        private PeerId FindPeerId(ValueHash256 hash)
        {
            // Check if we have the mapping cached
            if (_peerIdCache.TryGetValue(hash, out var peerId))
            {
                return peerId;
            }

            // This is a simple implementation that assumes the ValueHash256 is directly derived from the PeerId
            // In a full implementation, we would need to maintain bidirectional mappings
            
            // For ValueHash256 instances derived directly from PeerId bytes, we can reverse the mapping
            byte[] peerIdBytes = hash.Bytes;
            peerId = new PeerId(peerIdBytes);
            
            // Cache the mapping for future use
            _peerIdCache[hash] = peerId;
            
            return peerId;
        }
        
        // Method to register a known mapping between a PeerId and its ValueHash256 representation
        public void RegisterPeerId(PeerId peerId, ValueHash256 hash)
        {
            _peerIdCache[hash] = peerId;
        }
    }
}