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
using Nethermind.Libp2p.Protocols;
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

            _logger.LogInformation("📡 CLIENT: Initiating PING to node {Node}", receiver);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogError("❌ CLIENT: Failed to establish session with {Node} for PING", receiver);
                throw new InvalidOperationException($"Failed to establish session with {receiver}");
            }

            _logger.LogInformation("✅ CLIENT: Session established for {Node}, dialing PING protocol /ipfs/kad/1.0.0/ping", receiver);

            var response = await session
                .DialAsync<RequestResponseProtocol<PingRequest, PingResponse>, PingRequest, PingResponse>(new PingRequest(), token)
                .ConfigureAwait(false);

            _logger.LogInformation("✅ CLIENT: PING completed successfully to {Node}", receiver);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⚠️ CLIENT: PING to {Node} cancelled", receiver);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CLIENT: PING to {Node} failed: {Error}", receiver, ex.Message);

            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                _logger.LogDebug("CLIENT: Removing failed session for {Node}", receiver);
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

            _logger.LogInformation("📡 CLIENT: Initiating FIND_NODE to {Node} for target {Target}", receiver, target);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogError("❌ CLIENT: Failed to establish session with {Node} for FIND_NODE", receiver);
                return Array.Empty<TNode>();
            }

            _logger.LogInformation("✅ CLIENT: Session established for {Node}, dialing FIND_NODE protocol /ipfs/kad/1.0.0/find_node", receiver);

            var request = BuildFindNeighboursRequest(target);

            var response = await session
                .DialAsync<RequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>, FindNeighboursRequest, FindNeighboursResponse>(request, token)
                .ConfigureAwait(false);

            var neighbours = new List<TNode>();
            foreach (var nodeProto in response.Neighbours)
            {
                if (ConvertProtoToNode(nodeProto) is { } node)
                {
                    neighbours.Add(node);
                }
            }

            _logger.LogInformation("✅ CLIENT: FIND_NODE returned {Count} neighbours from {Node}", neighbours.Count, receiver);
            return neighbours.ToArray();
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⚠️ CLIENT: FIND_NODE from {Node} cancelled", receiver);
            return Array.Empty<TNode>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CLIENT: FIND_NODE from {Node} failed: {Error}", receiver, ex.Message);

            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                _logger.LogDebug("CLIENT: Removing failed session for {Node}", receiver);
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
            _logger.LogDebug("♻️ CLIENT: Reusing existing session for {Node}", node);
            return existing;
        }

        var multiaddress = GetMultiaddressForNode(node);
        if (multiaddress is null)
        {
            _logger.LogError("❌ CLIENT: Cannot get multiaddress for node {Node}", node);
            return null;
        }

        _logger.LogInformation("🔗 CLIENT: Creating new session to {Node} at {Multiaddr}",
            node, multiaddress);

        try
        {
            _logger.LogDebug("CLIENT: Calling DialAsync to {Multiaddr}", multiaddress);
            var session = await _localPeer.DialAsync(multiaddress, token).ConfigureAwait(false);

            _logger.LogInformation("✅ CLIENT: Session created successfully for {Node}", node);

            if (_activeSessions.TryAdd(node, session))
            {
                return session;
            }

            // Race condition: another task created session first
            if (_activeSessions.TryGetValue(node, out var cached))
            {
                _logger.LogDebug("CLIENT: Using cached session from race condition for {Node}", node);
                await SafeDisconnectAsync(session).ConfigureAwait(false);
                return cached;
            }

            return session;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⚠️ CLIENT: Session creation to {Node} was cancelled", node);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CLIENT: Failed to create session to {Node}: {Error}", node, ex.Message);
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

    /// <summary>
    /// Store a value on a remote peer.
    /// </summary>
    public async Task<bool> PutValue(TNode receiver, byte[] key, byte[] value, byte[]? signature = null, long? timestamp = null, byte[]? publisher = null, CancellationToken token = default)
    {
        ThrowIfDisposed();

        var acquired = false;
        try
        {
            await _connectionSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            _logger.LogDebug("📡 CLIENT: Initiating PUT_VALUE to {Node}", receiver);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogWarning("❌ CLIENT: Failed to establish session with {Node} for PUT_VALUE", receiver);
                return false;
            }

            var request = new PutValueRequest
            {
                Key = Google.Protobuf.ByteString.CopyFrom(key),
                Value = Google.Protobuf.ByteString.CopyFrom(value),
                Timestamp = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            };

            if (signature != null)
            {
                request.Signature = Google.Protobuf.ByteString.CopyFrom(signature);
            }

            if (publisher != null)
            {
                request.Publisher = Google.Protobuf.ByteString.CopyFrom(publisher);
            }

            var response = await session
                .DialAsync<RequestResponseProtocol<PutValueRequest, PutValueResponse>, PutValueRequest, PutValueResponse>(request, token)
                .ConfigureAwait(false);

            if (response.Success)
            {
                _logger.LogInformation("✅ CLIENT: PUT_VALUE completed successfully to {Node}", receiver);
            }
            else
            {
                _logger.LogWarning("⚠️ CLIENT: PUT_VALUE to {Node} returned error: {Error}", receiver, response.Error);
            }

            return response.Success;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⚠️ CLIENT: PUT_VALUE to {Node} cancelled", receiver);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CLIENT: PUT_VALUE to {Node} failed: {Error}", receiver, ex.Message);

            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                await SafeDisconnectAsync(failedSession).ConfigureAwait(false);
            }

            return false;
        }
        finally
        {
            if (acquired)
            {
                _connectionSemaphore.Release();
            }
        }
    }

    /// <summary>
    /// Retrieve a value from a remote peer.
    /// </summary>
    public async Task<(bool found, byte[]? value, byte[]? signature, long timestamp, byte[]? publisher)> GetValue(TNode receiver, byte[] key, CancellationToken token = default)
    {
        ThrowIfDisposed();

        var acquired = false;
        try
        {
            await _connectionSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            _logger.LogDebug("📡 CLIENT: Initiating GET_VALUE from {Node}", receiver);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogWarning("❌ CLIENT: Failed to establish session with {Node} for GET_VALUE", receiver);
                return (false, null, null, 0, null);
            }

            var request = new GetValueRequest
            {
                Key = Google.Protobuf.ByteString.CopyFrom(key)
            };

            var response = await session
                .DialAsync<RequestResponseProtocol<GetValueRequest, GetValueResponse>, GetValueRequest, GetValueResponse>(request, token)
                .ConfigureAwait(false);

            if (response.Found)
            {
                _logger.LogInformation("✅ CLIENT: GET_VALUE found value from {Node}", receiver);
                return (
                    true,
                    response.Value?.ToByteArray(),
                    response.Signature?.ToByteArray(),
                    response.Timestamp,
                    response.Publisher?.ToByteArray()
                );
            }
            else
            {
                _logger.LogDebug("CLIENT: GET_VALUE from {Node} - value not found", receiver);
                return (false, null, null, 0, null);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⚠️ CLIENT: GET_VALUE from {Node} cancelled", receiver);
            return (false, null, null, 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ CLIENT: GET_VALUE from {Node} failed: {Error}", receiver, ex.Message);

            if (_activeSessions.TryRemove(receiver, out var failedSession))
            {
                await SafeDisconnectAsync(failedSession).ConfigureAwait(false);
            }

            return (false, null, null, 0, null);
        }
        finally
        {
            if (acquired)
            {
                _connectionSemaphore.Release();
            }
        }
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
