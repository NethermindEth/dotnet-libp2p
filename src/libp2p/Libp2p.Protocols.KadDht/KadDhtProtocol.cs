using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Context;
using Google.Protobuf;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Libp2p.Protocols.KadDht;

    /// <summary>
    /// Implementation of the Kademlia DHT protocol for libp2p.
    /// </summary>
    public class KadDhtProtocol : ISessionProtocol, IContentRouter, IKademliaMessageSender<PeerId, ValueHash256>, IKademliaMessageSender<ValueHash256, ValueHash256>
    {
        private readonly ILogger<KadDhtProtocol> _logger;
        private readonly KadDhtOptions _options;
        private readonly IKademlia<PeerId, ValueHash256> _kademlia;
        private readonly IHost _host;
        private readonly PeerIdKeyOperator _keyOperator;

        /// <summary>
        /// Protocol ID for the Kademlia DHT protocol.
        /// </summary>
        public string Id => _options.ProtocolId;

        /// <summary>
        /// Creates a new instance of KadDhtProtocol.
        /// </summary>
        /// <param name="logger">Logger for this class.</param>
        /// <param name="options">DHT options.</param>
        /// <param name="kademlia">Kademlia implementation.</param>
        /// <param name="host">libp2p host.</param>
        /// <param name="keyOperator">Key operator for PeerId.</param>
        public KadDhtProtocol(
            ILogger<KadDhtProtocol> logger,
            IOptions<KadDhtOptions> options,
            IKademlia<PeerId, ValueHash256> kademlia,
            IHost host,
            PeerIdKeyOperator keyOperator)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _kademlia = kademlia ?? throw new ArgumentNullException(nameof(kademlia));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _keyOperator = keyOperator ?? throw new ArgumentNullException(nameof(keyOperator));
        }

        /// <summary>
        /// Handles incoming connections when listening.
        /// </summary>
        /// <param name="channel">The communication channel.</param>
        /// <param name="context">The session context.</param>
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {

            var sessionContext = context.UpgradeToSession();
            // TODO: Fix: INewSessionContext does not have PeerId. You may need to extract PeerId from context or elsewhere.
            PeerId? remotePeerId = null; // Placeholder, fix as per your context extraction logic.

            try
            {
                _logger.LogDebug("Handling incoming connection from {PeerId}", remotePeerId);
                
            // Add the peer to the routing table
            // TODO: _kademlia.AddOrRefresh expects ValueHash256, but remotePeerId is PeerId. Fix as needed.
            // _kademlia.AddOrRefresh(remotePeerId);

                while (!sessionContext.Token.IsCancellationRequested)
                {
                // Read the message length (varint)
                int messageLength = await channel.ReadVarintAsync(sessionContext.Token);
                // Read the message bytes
                var messageBytes = await channel.ReadAsync(messageLength, ReadBlockingMode.Blocking); // Use correct ReadBlockingMode
                // Parse the message
                var message = Message.Parser.ParseFrom(messageBytes);
                // Handle the message
                var response = await HandleMessageAsync(message, remotePeerId, sessionContext.Token);
                // Send the response
                if (response != null)
                {
                    var responseBytes = response.ToByteArray();
                    await channel.WriteVarintAsync(responseBytes.Length); // Assuming WriteVarintAsync takes only length
                    await channel.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(responseBytes)); // If WriteAsync expects ReadOnlySequence<byte>
                }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal cancellation, ignore
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling KadDHT connection from {PeerId}", remotePeerId);
            }
        }

        /// <summary>
        /// Initiates a connection to a remote peer.
        /// </summary>
        /// <param name="channel">The communication channel.</param>
        /// <param name="context">The session context.</param>
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            // This is a symmetric protocol, so we use the same logic for both dial and listen
            await ListenAsync(channel, context);
        }

        private async Task<Message> HandleMessageAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Received {MessageType} message from {PeerId}", message.Type, remotePeerId);

            switch (message.Type)
            {
                case MessageType.Ping:
                    return await HandlePingAsync(message, remotePeerId, cancellationToken);
                
                case MessageType.FindNode:
                    return await HandleFindNodeAsync(message, remotePeerId, cancellationToken);
                
                case MessageType.GetValue:
                    return await HandleGetValueAsync(message, remotePeerId, cancellationToken);
                
                case MessageType.PutValue:
                    return await HandlePutValueAsync(message, remotePeerId, cancellationToken);
                
                case MessageType.AddProvider:
                    return await HandleAddProviderAsync(message, remotePeerId, cancellationToken);
                
                case MessageType.GetProviders:
                    return await HandleGetProvidersAsync(message, remotePeerId, cancellationToken);
                
                default:
                    _logger.LogWarning("Received unknown message type {MessageType} from {PeerId}", message.Type, remotePeerId);
                    return null;
            }
        }

        private Task<Message> HandlePingAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Handling ping from {PeerId}", remotePeerId);
            
            // Simply respond with a ping message
            return Task.FromResult(new Message
            {
                Type = MessageType.Ping
            });
        }

        private async Task<Message> HandleFindNodeAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Handling find node from {PeerId} for key {Key}", remotePeerId, 
                BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

            if (!_options.EnableServerMode)
            {
                _logger.LogDebug("Server mode disabled, not handling find node request");
                return new Message { Type = MessageType.FindNode, Key = message.Key }; // Return empty response instead of null
            }

            // Create the response with the same key
            var response = new Message
            {
                Type = MessageType.FindNode,
                Key = message.Key
            };

            try
            {
                // Extract the target key from the message
                var keyBytes = message.Key.ToByteArray();
                var targetPeerId = new PeerId(keyBytes);
                
                // Use the Kademlia implementation to find closest peers
                var closestPeers = _kademlia.GetKNeighbour(targetPeerId);

                // Add each peer to the response
                foreach (var peer in closestPeers)
                {
                    if (peer != null)
                    {
                        // Convert ValueHash256 to PeerId
                        var peerBytes = peer.Bytes;
                        var peerRecord = new Peer 
                        { 
                            Id = Google.Protobuf.ByteString.CopyFrom(peerBytes)
                        };
                        
                        response.CloserPeers.Add(peerRecord);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling find node request");
            }

            return response;
        }

        private async Task<Message> HandleGetValueAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Handling get value from {PeerId} for key {Key}", remotePeerId, 
                BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

            if (!_options.EnableServerMode)
            {
                _logger.LogDebug("Server mode disabled, not handling get value request");
                return null;
            }
            
            // Convert the key to ValueHash256
            var keyBytes = EnsureLength(message.Key.ToByteArray(), 32);
            var keyPeerId = new PeerId(keyBytes); // Assuming PeerId can be constructed from bytes
            // TODO: _kademlia.GetValueAsync does not exist. Replace with correct method or remove.
            // var value = await _kademlia.GetValueAsync(keyHash, cancellationToken);
            var response = new Message
            {
                Type = MessageType.GetValue,
                Key = message.Key
            };
            // TODO: Implement value lookup logic. For now, always return closest peers.
            var closestPeers = _kademlia.GetKNeighbour(keyPeerId);
            foreach (var peer in closestPeers)
            {
                // TODO: peer is likely ValueHash256, not PeerId. Adjust as needed.
                response.CloserPeers.Add(new Peer { Id = ByteString.CopyFrom(peer.Bytes) });
            }
            return response;
        }

        private Task<Message> HandlePutValueAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Handling put value from {PeerId} for key {Key}", remotePeerId, 
                BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

            if (!_options.EnableServerMode || !_options.EnableValueStorage)
            {
                _logger.LogDebug("Server mode or value storage disabled, not handling put value request");
                return new Message { Type = MessageType.PutValue, Key = message.Key };
            }
            if (message.Record == null)
            {
                _logger.LogWarning("Received put value request without record from {PeerId}", remotePeerId);
                return new Message { Type = MessageType.PutValue, Key = message.Key };
            }
            // TODO: _kademlia.PutValueAsync does not exist. Replace with correct method or remove.
            // var keyHash = new ValueHash256(EnsureLength(message.Key.ToByteArray(), 32));
            // await _kademlia.PutValueAsync(keyHash, message.Record.Value.ToByteArray(), cancellationToken);
            return Task.FromResult(new Message
            {
                Type = MessageType.PutValue,
                Key = message.Key
            });
        }

        private Task<Message> HandleAddProviderAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Handling add provider from {PeerId} for key {Key}", remotePeerId, 
                BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

            if (!_options.EnableServerMode || !_options.EnableProviderStorage)
            {
                _logger.LogDebug("Server mode or provider storage disabled, not handling add provider request");
                return System.Threading.Tasks.Task.FromResult(new Message { Type = MessageType.AddProvider, Key = message.Key });
            }
            // TODO: Implement provider storage logic
            return System.Threading.Tasks.Task.FromResult(new Message { Type = MessageType.AddProvider, Key = message.Key });
        }

        private Task<Message> HandleGetProvidersAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Handling get providers from {PeerId} for key {Key}", remotePeerId, 
                BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

            if (!_options.EnableServerMode)
            {
                _logger.LogDebug("Server mode disabled, not handling get providers request");
                return System.Threading.Tasks.Task.FromResult(new Message { Type = MessageType.GetProviders, Key = message.Key });
            }
            // TODO: _kademlia.GetProvidersAsync does not exist. Replace with correct method or remove.
            // var keyHash = new ValueHash256(EnsureLength(message.Key.ToByteArray(), 32));
            // var providers = await _kademlia.GetProvidersAsync(keyHash, cancellationToken);
            var response = new Message
            {
                Type = MessageType.GetProviders,
                Key = message.Key
            };
            // TODO: Implement provider lookup logic. For now, always return closest peers.
            var keyBytes = EnsureLength(message.Key.ToByteArray(), 32);
            var keyPeerId = new PeerId(keyBytes);
            var closestPeers = _kademlia.GetKNeighbour(keyPeerId);
            foreach (var peer in closestPeers)
            {
                // TODO: peer is likely ValueHash256, not PeerId. Adjust as needed.
                response.CloserPeers.Add(new Peer { Id = ByteString.CopyFrom(peer.Bytes) });
            }
            return Task.FromResult(response);
        }

        private static byte[] EnsureLength(byte[] data, int length)
        {
            if (data.Length == length)
            {
                return data;
            }
            
            var result = new byte[length];
            
            if (data.Length < length)
            {
                Array.Copy(data, 0, result, 0, data.Length);
            }
            else
            {
                Array.Copy(data, 0, result, 0, length);
            }
            
            return result;
        }

        #region IContentRouter Implementation

        /// <summary>
        /// Finds providers for the specified content.
        /// </summary>
        /// <param name="contentId">The content identifier.</param>
        /// <param name="limit">The maximum number of providers to return.</param>
        /// <returns>A collection of peer IDs that provide the content.</returns>
        public async ValueTask<IEnumerable<PeerId>> FindProvidersAsync(byte[] contentId, int limit = 20)
        {
            if (!_options.EnableClientMode)
            {
                _logger.LogWarning("Client mode disabled, not finding providers");
                return Array.Empty<PeerId>();
            }

            var keyHash = _keyOperator.GetKeyHash(contentId);
            // TODO: Implement provider lookup logic
            return Array.Empty<PeerId>();
        }

        /// <summary>
        /// Provides the specified content to the network.
        /// </summary>
        /// <param name="contentId">The content identifier.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async ValueTask ProvideAsync(byte[] contentId)
        {
            if (!_options.EnableClientMode)
            {
                _logger.LogWarning("Client mode disabled, not providing content");
                return;
            }

            var keyHash = _keyOperator.GetKeyHash(contentId);
            var localPeerId = _host.GetPeerId();
            
            // TODO: Implement provider add logic
        }

        /// <summary>
        /// Puts a value in the DHT.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async ValueTask PutValueAsync(byte[] key, byte[] value)
        {
            if (!_options.EnableClientMode)
            {
                _logger.LogWarning("Client mode disabled, not putting value");
                return;
            }

            var keyHash = _keyOperator.GetKeyHash(key);
            // TODO: Implement value put logic
        }

        /// <summary>
        /// Gets a value from the DHT.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The value, or null if not found.</returns>
        public async ValueTask<byte[]?> GetValueAsync(byte[] key)
        {
            if (!_options.EnableClientMode)
            {
                _logger.LogWarning("Client mode disabled, not getting value");
                return null;
            }

            var keyHash = _keyOperator.GetKeyHash(key);
            // TODO: Implement value get logic
            return null;
        }

        #endregion

        #region IKademliaMessageSender Implementation

        /// <summary>
        /// Sends a ping message to a remote peer.
        /// </summary>
        /// <param name="receiver">The receiver peer.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task Ping(PeerId receiver, CancellationToken token)
        {
            try
            {
                _logger.LogDebug("Sending ping to {PeerId}", receiver);
                
                // Create a message
                var message = new Message
                {
                    Type = MessageType.Ping
                };
                
                // Send the message
                var response = await SendMessageAsync(receiver, message, token);
                
                // Check if we got a response
                if (response == null || response.Type != MessageType.Ping)
                {
                    _logger.LogWarning("No valid ping response from {PeerId}", receiver);
                    throw new Exception($"No valid ping response from {receiver}");
                }
                
                // The peer is alive - add or refresh it in our routing table
                var receiverHash = _keyOperator.GetKey(receiver);
                _kademlia.AddOrRefresh(receiverHash);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending ping to {PeerId}", receiver);
                throw;
            }
        }

        /// <summary>
        /// Sends a find neighbors message to a remote peer (PeerId version).
        /// </summary>
        /// <param name="receiver">The receiver peer ID.</param>
        /// <param name="target">The target key.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>An array of peers closest to the target.</returns>
        public async Task<ValueHash256[]> FindNeighbours(PeerId receiver, ValueHash256 target, CancellationToken token)
        {
            try
            {
                _logger.LogDebug("Sending find neighbors to {PeerId} for key {Key}", receiver, 
                    BitConverter.ToString(target.Bytes).Replace("-", ""));
                        
                // Create a message
                var message = new Message
                {
                    Type = MessageType.FindNode,
                    Key = Google.Protobuf.ByteString.CopyFrom(target.Bytes)
                };
                
                // Send the message
                var response = await SendMessageAsync(receiver, message, token);
                
                // Check if we got a response
                if (response == null || response.Type != MessageType.FindNode)
                {
                    _logger.LogWarning("No valid find neighbors response from {PeerId}", receiver);
                    return Array.Empty<ValueHash256>();
                }
                
                // Extract the peers from the response
                var peers = response.CloserPeers
                    .Select(p => {
                        var peerBytes = p.Id.ToByteArray();
                        return new ValueHash256(peerBytes);
                    })
                    .ToArray();

                // Add discovered peers to our routing table
                foreach (var peer in peers)
                {
                    _kademlia.AddOrRefresh(peer);
                }
                
                return peers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending find neighbors to {PeerId}", receiver);
                return Array.Empty<ValueHash256>();
            }
        }

        /// <summary>
        /// Sends a message to a peer and waits for a response.
        /// </summary>
        /// <param name="peerId">The peer ID to communicate with.</param>
        /// <param name="message">The message to send.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>The response message.</returns>
        private async Task<Message> SendMessageAsync(PeerId peerId, Message message, CancellationToken token)
        {
            ISession session = null;
            try
            {
                // Dial the peer
                session = await _host.DialPeerAsync(peerId, token);
                if (session == null)
                {
                    _logger.LogError("Failed to dial peer {PeerId}", peerId);
                    throw new InvalidOperationException($"Failed to dial peer {peerId}");
                }
                
                // Create a new channel for the protocol
                var channel = await session.OpenStreamAsync(Id, token);
                if (channel == null)
                {
                    _logger.LogError("Failed to create channel for protocol {ProtocolId}", Id);
                    throw new InvalidOperationException($"Failed to create channel for protocol {Id}");
                }
                
                try
                {
                    // Serialize the message
                    var messageBytes = message.ToByteArray();
                    
                    // Write the message length (varint)
                    await channel.WriteVarintAsync(messageBytes.Length, token);
                    
                    // Write the message bytes
                    await channel.WriteAsync(messageBytes, token);
                    
                    // Read the response length (varint)
                    var responseLength = await channel.ReadVarintAsync(token);
                    
                    // Read the response bytes
                    var responseBytes = await channel.ReadExactlyAsync(responseLength, token);
                    
                    // Parse the response
                    return Message.Parser.ParseFrom(responseBytes);
                }
                finally
                {
                    await channel.CloseAsync(token);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to peer {PeerId}", peerId);
                throw; // Rethrow so caller can handle appropriately
            }
        }

        #endregion

        /// <summary>
        /// Sends a ping message to a remote peer (ValueHash256 version).
        /// </summary>
        /// <param name="receiver">The receiver peer ID.</param>
        /// <param name="token">The cancellation token.</param>
        public async Task Ping(ValueHash256 receiver, CancellationToken token)
        {
            try
            {
                _logger.LogDebug("Sending ping to {PeerId}", receiver);

                // Convert ValueHash256 to PeerId
                PeerId peerId = FindPeerId(receiver);

                // Create a session with the remote peer
                // TODO: Implement peer session dial logic

                // Create a message
                var message = new Message
                {
                    Type = MessageType.Ping
                };

                // Send the message
                var response = await SendMessageAsync(peerId, message, token);

                // Check if we got a response
                if (response == null || response.Type != MessageType.Ping)
                {
                    _logger.LogWarning("No valid ping response from {PeerId}", receiver);
                    throw new Exception($"No valid ping response from {receiver}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending ping to {PeerId}", receiver);
                throw;
            }
        }

        /// <summary>
        /// Sends a find neighbors message to a remote peer (ValueHash256 version).
        /// </summary>
        /// <param name="receiver">The receiver peer ID.</param>
        /// <param name="target">The target key.</param>
        /// <param name="token">The cancellation token.</param>
        /// <returns>An array of peers closest to the target.</returns>
        public async Task<ValueHash256[]> FindNeighbours(ValueHash256 receiver, ValueHash256 target, CancellationToken token)
        {
            try
            {
                _logger.LogDebug("Sending find neighbors to {PeerId} for key {Key}", receiver,
                    BitConverter.ToString(target.Bytes).Replace("-", ""));

                // Convert ValueHash256 to PeerId
                PeerId peerId = FindPeerId(receiver);

                // Create a session with the remote peer
                // TODO: Implement peer session dial logic

                // Create a message
                var message = new Message
                {
                    Type = MessageType.FindNode,
                    Key = ByteString.CopyFrom(target.Bytes)
                };

                // Send the message
                var response = await SendMessageAsync(peerId, message, token);

                // Check if we got a response
                if (response == null || response.Type != MessageType.FindNode)
                {
                    _logger.LogWarning("No valid find neighbors response from {PeerId}", receiver);
                    return Array.Empty<ValueHash256>();
                }

                // Extract the peers from the response
                var peers = response.CloserPeers.Select(p => new PeerId(p.Id.ToByteArray())).ToArray();

                // Convert to ValueHash256 array
                return peers.Select(p => _keyOperator.GetKey(p)).ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending find neighbors to {PeerId}", receiver);
                return Array.Empty<ValueHash256>();
            }
        }

        private PeerId FindPeerId(ValueHash256 hash)
        {
            // This is a placeholder implementation. In a real implementation, you would
            // need to maintain a mapping between ValueHash256 and PeerId.
            // For now, we just throw an exception.
            throw new NotImplementedException("Reverse lookup from ValueHash256 to PeerId is not implemented.");
        }
    }
// End of file