// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.RequestResponse;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using Nethermind.Libp2p.Core.Dto;

namespace Libp2p.Protocols.KadDht.Network;

/// <summary>
/// Real libp2p implementation of Kademlia message sender using actual networking protocols.
/// </summary>
public class LibP2pKademliaMessageSender<TPublicKey, TNode> : IKademliaMessageSender<TPublicKey, TNode>, IDisposable, IAsyncDisposable
    where TPublicKey : notnull
    where TNode : class, IComparable<TNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<LibP2pKademliaMessageSender<TPublicKey, TNode>> _logger;
    private readonly ConcurrentDictionary<TNode, ISession> _activeSessions = new();
    private readonly SemaphoreSlim _connectionSemaphore = new(Environment.ProcessorCount);

    public LibP2pKademliaMessageSender(ILocalPeer localPeer, ILoggerFactory? loggerFactory = null)
    {
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<LibP2pKademliaMessageSender<TPublicKey, TNode>>() ?? 
                  Microsoft.Extensions.Logging.Abstractions.NullLogger<LibP2pKademliaMessageSender<TPublicKey, TNode>>.Instance;
    }

    /// <summary>
    /// Send a ping message to a peer using libp2p DHT protocol.
    /// </summary>
    public async Task Ping(TNode receiver, CancellationToken token)
    {
        await _connectionSemaphore.WaitAsync(token);
        try
        {
            _logger.LogDebug("Pinging node {Node} via libp2p", receiver);

            var session = await GetOrCreateSession(receiver, token);
            if (session == null)
            {
                throw new InvalidOperationException($"Failed to establish session with {receiver}");
            }

            // Use the ping protocol to send a real DHT ping message
            var response = await session.DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(
                new PingRequest(), token);
            
            _logger.LogTrace("Ping to {Node} completed successfully", receiver);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ping to {Node} failed: {Error}", receiver, ex.Message);
            
            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                try { await failedSession.DisconnectAsync(); } catch { }
            }
            throw;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Find nearest neighbours to a target key using real libp2p DHT protocol.
    /// </summary>
    public async Task<TNode[]> FindNeighbours(TNode receiver, TPublicKey target, CancellationToken token)
    {
        await _connectionSemaphore.WaitAsync(token);
        try
        {
            _logger.LogDebug("Finding neighbours from {Node} for target {Target} via libp2p", receiver, target);

            var session = await GetOrCreateSession(receiver, token);
            if (session == null)
            {
                _logger.LogWarning("Failed to establish session with {Node} for FindNeighbours", receiver);
                return Array.Empty<TNode>();
            }

            // Convert target key to protobuf format
            var targetBytes = ConvertKeyToBytes(target);
            var request = new FindNeighboursRequest 
            { 
                Target = new PublicKeyBytes { Value = Google.Protobuf.ByteString.CopyFrom(targetBytes) }
            };

            var response = await session.DialAsync<KadDhtFindNeighboursProtocol, FindNeighboursRequest, FindNeighboursResponse>(
                request, token);

            // Convert protobuf response back to TNode objects
            var neighbours = new List<TNode>();
            foreach (var nodeProto in response.Neighbours)
            {
                if (ConvertProtoToNode(nodeProto) is TNode node)
                {
                    neighbours.Add(node);
                }
            }

            _logger.LogTrace("Found {Count} neighbours from {Node}", neighbours.Count, receiver);
            return neighbours.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("FindNeighbours from {Node} failed: {Error}", receiver, ex.Message);
            
            // Remove failed session
            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                try { await failedSession.DisconnectAsync(); } catch { }
            }
            
            return Array.Empty<TNode>();
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    /// <summary>
    /// Get or create a session to the specified node.
    /// </summary>
    private async Task<ISession?> GetOrCreateSession(TNode node, CancellationToken token)
    {
        // Check if we already have an active session
        if (_activeSessions.TryGetValue(node, out var existingSession))
        {
            return existingSession;
        }

        try
        {
            // Get multiaddress for the node
            var multiaddress = GetMultiaddressForNode(node);
            if (multiaddress == null)
            {
                _logger.LogWarning("No valid multiaddress found for node {Node}", node);
                return null;
            }

            var session = await _localPeer.DialAsync(multiaddress, token);
            
            // Cache the session
            _activeSessions[node] = session;
            
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create session to {Node}: {Error}", node, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Convert a target key to bytes for protobuf serialization.
    /// </summary>
    private byte[] ConvertKeyToBytes(TPublicKey key)
    {
        // This is a simplified conversion - in real implementation this would depend on TKey type
        if (key is byte[] bytes) return bytes;
        if (key is string str) return System.Text.Encoding.UTF8.GetBytes(str);
        
        // Fallback: serialize as string
        return System.Text.Encoding.UTF8.GetBytes(key.ToString() ?? string.Empty);
    }

    /// <summary>
    /// Convert a protobuf Node to TNode type.
    /// </summary>
    private TNode? ConvertProtoToNode(Node nodeProto)
    {
        // This conversion depends on the actual TNode type
        // For demo purposes, we assume TNode can be constructed from the proto data
        
        // If TNode is DhtNode, we can construct it directly
        if (typeof(TNode).Name == nameof(Integration.DhtNode))
        {
            try
            {
                var libp2pPublicKey = Nethermind.Libp2p.Core.Dto.PublicKey.Parser.ParseFrom(nodeProto.PublicKey.ToByteArray());
                var peerId = new PeerId(libp2pPublicKey);
                
                var kademliaPublicKey = new Kademlia.PublicKey(nodeProto.PublicKey.ToByteArray());
                
                var multiaddrs = nodeProto.Multiaddrs.ToArray();

                var dhtNode = new Integration.DhtNode
                {
                    PeerId = peerId,
                    PublicKey = kademliaPublicKey,
                    Multiaddrs = multiaddrs
                };
                
                return (TNode)(object)dhtNode;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to convert proto node to DhtNode: {Error}", ex.Message);
                return null;
            }
        }
        
        _logger.LogWarning("Unsupported node type conversion: {Type}", typeof(TNode).Name);
        return null;
    }

    /// <summary>
    /// Get multiaddress for a node.
    /// </summary>
    private Multiaddress? GetMultiaddressForNode(TNode node)
    {
        if (node is Integration.DhtNode dhtNode && dhtNode.Multiaddrs.Count > 0)
        {
            // Try each multiaddress until we find one that works
            foreach (var multiaddr in dhtNode.Multiaddrs)
            {
                try
                {
                    _logger.LogDebug("Attempting to decode multiaddress: '{Multiaddr}' for node {Node}", multiaddr, node);
                    return Multiaddress.Decode(multiaddr);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Failed to decode multiaddress '{Multiaddr}': {Error}", multiaddr, ex.Message);
                }
            }
            
            _logger.LogWarning("No valid multiaddress found for node {Node}", node);
            return null;
        }
        
        _logger.LogWarning("Cannot extract multiaddress from node type {Type}", typeof(TNode).Name);
        return null;
    }

    /// <summary>
    /// Dispose of all active sessions.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var session in _activeSessions.Values)
        {
            try
            {
                await session.DisconnectAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Error disposing session: {Error}", ex.Message);
            }
        }
        
        _activeSessions.Clear();
        _connectionSemaphore.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().Wait();
    }
}
