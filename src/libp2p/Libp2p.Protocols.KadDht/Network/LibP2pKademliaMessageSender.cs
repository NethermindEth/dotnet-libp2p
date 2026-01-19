// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.RequestResponse;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Network;

/// <summary>
/// Real libp2p implementation of Kademlia message sender using actual networking protocols.
/// </summary>
public sealed class LibP2pKademliaMessageSender<TPublicKey, TNode> : IKademliaMessageSender<TPublicKey, TNode>, IDisposable, IAsyncDisposable
    where TPublicKey : notnull
    where TNode : class, IComparable<TNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<LibP2pKademliaMessageSender<TPublicKey, TNode>> _logger;
    private readonly ConcurrentDictionary<TNode, ISession> _activeSessions = new();
    private readonly SemaphoreSlim _connectionSemaphore;
    private bool _disposed;

    public LibP2pKademliaMessageSender(ILocalPeer localPeer, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(localPeer);
        _localPeer = localPeer;

        var factory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<LibP2pKademliaMessageSender<TPublicKey, TNode>>();

        _connectionSemaphore = new SemaphoreSlim(Environment.ProcessorCount);
    }

    public async Task Ping(TNode receiver, CancellationToken token)
    {
        ThrowIfDisposed();

        var acquired = false;
        try
        {
            await _connectionSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            _logger.LogDebug("Pinging node {Node} via libp2p", receiver);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                throw new InvalidOperationException($"Failed to establish session with {receiver}");
            }

            _logger.LogInformation("Session established for {Node}, now dialing DHT ping protocol...", receiver);

            await session
                .DialAsync<KadDhtPingProtocol, PingRequest, PingResponse>(new PingRequest(), token)
                .ConfigureAwait(false);

            _logger.LogInformation("✅ Ping to {Node} completed successfully", receiver);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Ping to {Node} cancelled", receiver);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ping to {Node} failed: {Error}", receiver, ex.Message);

            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                await SafeDisconnectAsync(failedSession).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (acquired)
            {
                _connectionSemaphore.Release();
            }
        }
    }

    public async Task<TNode[]> FindNeighbours(TNode receiver, TPublicKey target, CancellationToken token)
    {
        ThrowIfDisposed();

        var acquired = false;
        try
        {
            await _connectionSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            _logger.LogDebug("Finding neighbours from {Node} for target {Target} via libp2p", receiver, target);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogWarning("Failed to establish session with {Node} for FindNeighbours", receiver);
                return Array.Empty<TNode>();
            }

            var request = BuildFindNeighboursRequest(target);

            var response = await session
                .DialAsync<KadDhtFindNeighboursProtocol, FindNeighboursRequest, FindNeighboursResponse>(request, token)
                .ConfigureAwait(false);

            var neighbours = new List<TNode>();
            foreach (var nodeProto in response.Neighbours)
            {
                if (ConvertProtoToNode(nodeProto) is { } node)
                {
                    neighbours.Add(node);
                }
            }

            _logger.LogTrace("Found {Count} neighbours from {Node}", neighbours.Count, receiver);
            return neighbours.ToArray();
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("FindNeighbours from {Node} cancelled", receiver);
            return Array.Empty<TNode>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("FindNeighbours from {Node} failed: {Error}", receiver, ex.Message);

            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                await SafeDisconnectAsync(failedSession).ConfigureAwait(false);
            }

            return Array.Empty<TNode>();
        }
        finally
        {
            if (acquired)
            {
                _connectionSemaphore.Release();
            }
        }
    }

    private FindNeighboursRequest BuildFindNeighboursRequest(TPublicKey target)
    {
        var targetBytes = ConvertKeyToBytes(target);
        return new FindNeighboursRequest
        {
            Target = new PublicKeyBytes { Value = Google.Protobuf.ByteString.CopyFrom(targetBytes) }
        };
    }

    private async Task<ISession?> GetOrCreateSession(TNode node, CancellationToken token)
    {
        if (_activeSessions.TryGetValue(node, out var existing))
        {
            return existing;
        }

        var multiaddress = GetMultiaddressForNode(node);
        if (multiaddress is null)
        {
            return null;
        }

        try
        {
            var session = await _localPeer.DialAsync(multiaddress, token).ConfigureAwait(false);

            if (_activeSessions.TryAdd(node, session))
            {
                return session;
            }

            if (_activeSessions.TryGetValue(node, out var cached))
            {
                await SafeDisconnectAsync(session).ConfigureAwait(false);
                return cached;
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to create session to {Node}: {Error}", node, ex.Message);
            return null;
        }
    }

    private static byte[] ConvertKeyToBytes(TPublicKey key)
    {
        switch (key)
        {
            case byte[] bytes:
                return bytes;
            case ReadOnlyMemory<byte> rom:
                return rom.ToArray();
            case PublicKey kadPublicKey:
                return kadPublicKey.Hash.Bytes;
            case ValueHash256 valueHash:
                return valueHash.Bytes;
            default:
                return Encoding.UTF8.GetBytes(key.ToString() ?? string.Empty);
        }
    }

    private TNode? ConvertProtoToNode(Node nodeProto)
    {
        if (typeof(TNode) != typeof(Integration.DhtNode))
        {
            _logger.LogWarning("Unsupported node type conversion: {Type}", typeof(TNode).Name);
            return null;
        }

        try
        {
            var publicKeyBytes = nodeProto.PublicKey.ToByteArray();
            if (publicKeyBytes.Length == 0)
            {
                _logger.LogDebug("Node proto missing public key");
                return null;
            }

            var kadPublicKey = new PublicKey(publicKeyBytes);
            var peerId = new PeerId(publicKeyBytes);

            var dhtNode = new Integration.DhtNode
            {
                PublicKey = kadPublicKey,
                PeerId = peerId,
                Multiaddrs = nodeProto.Multiaddrs.Count > 0 ? nodeProto.Multiaddrs.ToArray() : Array.Empty<string>()
            };

            return (TNode)(object)dhtNode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to convert proto node to DhtNode");
            return null;
        }
    }

    private Multiaddress? GetMultiaddressForNode(TNode node)
    {
        if (node is not Integration.DhtNode dhtNode)
        {
            _logger.LogWarning("Cannot extract multiaddress from node type {Type}", typeof(TNode).Name);
            return null;
        }

        foreach (var raw in dhtNode.Multiaddrs)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            try
            {
                _logger.LogDebug("Attempting to decode multiaddress: '{Multiaddr}' for node {Node}", raw, node);
                return Multiaddress.Decode(raw);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Failed to decode multiaddress '{Multiaddr}': {Error}", raw, ex.Message);
            }
        }

        _logger.LogWarning("No valid multiaddress found for node {Node}", node);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var sessions = _activeSessions.ToArray();
        foreach (var (_, session) in sessions)
        {
            await SafeDisconnectAsync(session).ConfigureAwait(false);
        }

        _activeSessions.Clear();
        _connectionSemaphore.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    private async Task SafeDisconnectAsync(ISession session)
    {
        try
        {
            await session.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error disposing session: {Error}", ex.Message);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LibP2pKademliaMessageSender<TPublicKey, TNode>));
        }
    }
}
