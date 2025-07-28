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
using System.IO;
using System.Buffers;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Implementation of the Kademlia DHT protocol for libp2p.
/// </summary>
public class KadDhtProtocol : ISessionProtocol, IContentRouter, IKademliaMessageSender<PeerId, ValueHash256>
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
                byte[] messageBytes = new byte[messageLength];
                await channel.ReadAsync(messageBytes, 0, messageLength, sessionContext.Token);
                // Parse the message
                var message = Message.Parser.ParseFrom(messageBytes);
                // Handle the message
                var response = await HandleMessageAsync(message, remotePeerId, sessionContext.Token);
                // Send the response
                if (response != null)
                {
                    var responseBytes = response.ToByteArray();
                    await channel.WriteVarintAsync(responseBytes.Length);
                    await channel.WriteAsync(new ReadOnlySequence<byte>(responseBytes));
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

    private async Task<Message> HandlePingAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling ping from {PeerId}", remotePeerId);

        // Simply respond with a ping message
        return await Task.FromResult(new Message
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
            return await Task.FromResult(new Message { Type = MessageType.FindNode, Key = message.Key }); // Return empty response instead of null
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

            // Convert PeerId to ValueHash256 using the key operator
            var targetKey = _keyOperator.GetKey(targetPeerId);

            // Use the Kademlia implementation to find closest peers
            var closestPeers = _kademlia.GetKNeighbour(targetKey);

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

        return await Task.FromResult(response);
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

        var response = new Message
        {
            Type = MessageType.GetValue,
            Key = message.Key
        };
        var key = _keyOperator.GetKey(keyPeerId);
        var closestPeers = _kademlia.GetKNeighbour(key);
        foreach (var peer in closestPeers)
        {
            response.CloserPeers.Add(new Peer { Id = ByteString.CopyFrom(peer.Bytes) });
        }
        return await Task.FromResult(response);
    }

    private async Task<Message> HandlePutValueAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling put value from {PeerId} for key {Key}", remotePeerId,
            BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

        if (!_options.EnableServerMode || !_options.EnableValueStorage)
        {
            _logger.LogDebug("Server mode or value storage disabled, not handling put value request");
            return await Task.FromResult(new Message { Type = MessageType.PutValue, Key = message.Key });
        }
        if (message.Record == null)
        {
            _logger.LogWarning("Received put value request without record from {PeerId}", remotePeerId);
            return await Task.FromResult(new Message { Type = MessageType.PutValue, Key = message.Key });
        }

        // TODO: Implement value storage logic
        return await Task.FromResult(new Message { Type = MessageType.PutValue, Key = message.Key });
    }

    private async Task<Message> HandleAddProviderAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling add provider from {PeerId} for key {Key}", remotePeerId,
            BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

        if (!_options.EnableServerMode || !_options.EnableProviderStorage)
        {
            _logger.LogDebug("Server mode or provider storage disabled, not handling add provider request");
            return await Task.FromResult(new Message { Type = MessageType.AddProvider, Key = message.Key });
        }

        // TODO: Implement provider storage logic
        return await Task.FromResult(new Message { Type = MessageType.AddProvider, Key = message.Key });
    }

    private async Task<Message> HandleGetProvidersAsync(Message message, PeerId remotePeerId, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Handling get providers from {PeerId} for key {Key}", remotePeerId,
            BitConverter.ToString(message.Key.ToByteArray()).Replace("-", ""));

        if (!_options.EnableServerMode || !_options.EnableProviderStorage)
        {
            _logger.LogDebug("Server mode or provider storage disabled, not handling get providers request");
            return await Task.FromResult(new Message { Type = MessageType.GetProviders, Key = message.Key });
        }

        // TODO: Implement provider lookup logic
        return await Task.FromResult(new Message { Type = MessageType.GetProviders, Key = message.Key });
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
            Buffer.BlockCopy(data, 0, result, 0, data.Length);
        }
        else
        {
            Buffer.BlockCopy(data, 0, result, 0, length);
        }

        return result;
    }

    public async ValueTask<IEnumerable<PeerId>> FindProvidersAsync(byte[] contentId, int limit = 20)
    {
        if (!_options.EnableClientMode)
        {
            _logger.LogDebug("Client mode disabled, not performing find providers");
            return Array.Empty<PeerId>();
        }

        // TODO: Implement find providers logic
        return Array.Empty<PeerId>();
    }

    public async ValueTask ProvideAsync(byte[] contentId)
    {
        if (!_options.EnableClientMode)
        {
            _logger.LogDebug("Client mode disabled, not performing provide");
            return;
        }

        // TODO: Implement provide logic
    }

    public async ValueTask PutValueAsync(byte[] key, byte[] value)
    {
        if (!_options.EnableClientMode)
        {
            _logger.LogDebug("Client mode disabled, not performing put value");
            return;
        }

        // TODO: Implement put value logic
    }

    public async ValueTask<byte[]?> GetValueAsync(byte[] key)
    {
        if (!_options.EnableClientMode)
        {
            _logger.LogDebug("Client mode disabled, not performing get value");
            return null;
        }

        // TODO: Implement get value logic
        return null;
    }

    /// <summary>
    /// Sends a ping message to a remote peer.
    /// </summary>
    public async Task Ping(PeerId receiver, CancellationToken token)
    {
        _logger.LogDebug("Sending ping to {PeerId}", receiver);

        var message = new Message
        {
            Type = MessageType.Ping
        };

        try
        {
            await SendMessageAsync(receiver, message, token);
            _logger.LogDebug("Successfully pinged {PeerId}", receiver);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ping {PeerId}", receiver);
            throw;
        }
    }

    /// <summary>
    /// Sends a find neighbors message to a remote peer.
    /// </summary>
    public async Task<PeerId[]> FindNeighbours(PeerId receiver, ValueHash256 target, CancellationToken token)
    {
        _logger.LogDebug("Finding neighbours from {PeerId} for target {Target}", receiver, target);

        var message = new Message
        {
            Type = MessageType.FindNode,
            Key = ByteString.CopyFrom(target.Bytes)
        };

        try
        {
            var response = await SendMessageAsync(receiver, message, token);
            if (response?.CloserPeers == null || !response.CloserPeers.Any())
            {
                return Array.Empty<PeerId>();
            }

            return response.CloserPeers
                .Select(peer => new PeerId(peer.Id.ToByteArray()))
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find neighbours from {PeerId} for target {Target}", receiver, target);
            throw;
        }
    }

    private async Task<Message> SendMessageAsync(PeerId peerId, Message message, CancellationToken token)
    {
        var session = await _host.DialPeerAsync(peerId, token);
        if (session == null)
        {
            throw new InvalidOperationException($"Could not establish session with peer {peerId}");
        }

        try
        {
            await session.DialAsync<KadDhtProtocol>(token);

            var messageBytes = message.ToByteArray();
            await session.WriteVarintAsync(messageBytes.Length);
            await session.WriteAsync(new ReadOnlySequence<byte>(messageBytes));

            var responseLength = await session.ReadVarintAsync(token);
            var responseBytes = new byte[responseLength];
            await session.ReadAsync(new ReadOnlySequence<byte>(responseBytes));

            return Message.Parser.ParseFrom(responseBytes);
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    public async Task Ping(ValueHash256 receiver, CancellationToken token)
    {
        if (!_options.EnableClientMode)
        {
            _logger.LogDebug("Client mode disabled, not performing ping");
            return;
        }

        var peerId = FindPeerId(receiver);
        if (peerId == null)
        {
            throw new InvalidOperationException($"Could not find PeerId for hash {receiver}");
        }

        var message = new Message
        {
            Type = MessageType.Ping
        };

        try
        {
            await SendMessageAsync(peerId, message, token);
            _kademlia.AddOrRefresh(receiver);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pinging peer {PeerId}", peerId);
            throw;
        }
    }

    public async Task<ValueHash256[]> FindNeighbours(ValueHash256 receiver, ValueHash256 target, CancellationToken token)
    {
        if (!_options.EnableClientMode)
        {
            _logger.LogDebug("Client mode disabled, not performing find neighbours");
            return Array.Empty<ValueHash256>();
        }

        var peerId = FindPeerId(receiver);
        if (peerId == null)
        {
            throw new InvalidOperationException($"Could not find PeerId for hash {receiver}");
        }

        var message = new Message
        {
            Type = MessageType.FindNode,
            Key = ByteString.CopyFrom(target.Bytes)
        };

        try
        {
            var response = await SendMessageAsync(peerId, message, token);
            if (response == null)
            {
                return Array.Empty<ValueHash256>();
            }

            var neighbours = response.CloserPeers
                .Select(p => new ValueHash256(p.Id.ToByteArray()))
                .ToArray();

            return neighbours;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding neighbours from peer {PeerId}", peerId);
            throw;
        }
    }

    private PeerId FindPeerId(ValueHash256 hash)
    {
        // TODO: Implement proper PeerId lookup logic
        return new PeerId(hash.Bytes);
    }
}
