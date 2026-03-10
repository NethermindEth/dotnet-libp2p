// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Text;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Network;

/// <summary>
/// Generic libp2p Kademlia message sender using the spec-compliant unified Message envelope.
/// All operations use the single /ipfs/kad/1.0.0 protocol with Message.Type dispatch.
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
        await WithSession(receiver, "PING", token, async session =>
        {
            var request = MessageHelper.CreatePingRequest();
            await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, token)
                .ConfigureAwait(false);
        });
    }

    public async Task<TNode[]> FindNeighbours(TNode receiver, TPublicKey target, CancellationToken token)
    {
        ThrowIfDisposed();
        TNode[] result = Array.Empty<TNode>();

        await WithSession(receiver, "FIND_NODE", token, async session =>
        {
            var request = MessageHelper.CreateFindNodeRequest(ConvertKeyToBytes(target));
            var response = await session
                .DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, token)
                .ConfigureAwait(false);

            var neighbours = new List<TNode>();
            foreach (var wirePeer in response.CloserPeers)
            {
                if (ConvertWirePeerToNode(wirePeer) is { } node)
                    neighbours.Add(node);
            }

            _logger.LogInformation("FIND_NODE returned {Count} neighbours from {Node}", neighbours.Count, receiver);
            result = neighbours.ToArray();
        });

        return result;
    }

    /// <summary>
    /// Store a value on a remote peer using the spec-compliant PUT_VALUE message.
    /// </summary>
    public async Task<bool> PutValue(TNode receiver, byte[] key, byte[] value,
        byte[]? signature = null, long? timestamp = null, byte[]? publisher = null,
        CancellationToken token = default)
    {
        ThrowIfDisposed();
        bool success = false;

        await WithSession(receiver, "PUT_VALUE", token, async session =>
        {
            var request = MessageHelper.CreatePutValueRequest(key, value,
                timestamp.HasValue
                    ? DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).ToString("o")
                    : null);

            var response = await session
                .DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, token)
                .ConfigureAwait(false);

            // Per spec, a successful PUT echoes back the record
            success = response.Record != null;
            if (success)
                _logger.LogInformation("PUT_VALUE completed to {Node}", receiver);
            else
                _logger.LogWarning("PUT_VALUE to {Node} returned no record echo", receiver);
        });

        return success;
    }

    /// <summary>
    /// Retrieve a value from a remote peer using the spec-compliant GET_VALUE message.
    /// </summary>
    public async Task<(bool found, byte[]? value, byte[]? signature, long timestamp, byte[]? publisher)> GetValue(
        TNode receiver, byte[] key, CancellationToken token = default)
    {
        ThrowIfDisposed();
        var result = (found: false, value: (byte[]?)null, signature: (byte[]?)null, timestamp: 0L, publisher: (byte[]?)null);

        await WithSession(receiver, "GET_VALUE", token, async session =>
        {
            var request = MessageHelper.CreateGetValueRequest(key);
            var response = await session
                .DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, token)
                .ConfigureAwait(false);

            if (response.Record != null && response.Record.Value != null && !response.Record.Value.IsEmpty)
            {
                _logger.LogInformation("GET_VALUE found value from {Node}", receiver);
                long ts = 0;
                if (response.Record.HasTimeReceived && DateTimeOffset.TryParse(response.Record.TimeReceived, out var dto))
                    ts = dto.ToUnixTimeSeconds();

                result = (true, response.Record.Value.ToByteArray(), null, ts, null);
            }
            else
            {
                _logger.LogDebug("GET_VALUE from {Node}: value not found, got {CloserPeers} closer peers",
                    receiver, response.CloserPeers.Count);
            }
        });

        return result;
    }

    private async Task WithSession(TNode receiver, string operation, CancellationToken token, Func<ISession, Task> action)
    {
        var acquired = false;
        try
        {
            await _connectionSemaphore.WaitAsync(token).ConfigureAwait(false);
            acquired = true;

            _logger.LogDebug("{Operation} to {Node}", operation, receiver);

            var session = await GetOrCreateSession(receiver, token).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogError("Failed to establish session with {Node} for {Operation}", receiver, operation);
                throw new InvalidOperationException($"Failed to establish session with {receiver}");
            }

            await action(session);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{Operation} to {Node} cancelled", operation, receiver);
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} to {Node} failed: {Error}", operation, receiver, ex.Message);
            if (_activeSessions.TryRemove(receiver, out var failedSession))
                await SafeDisconnectAsync(failedSession).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (acquired) _connectionSemaphore.Release();
        }
    }

    private async Task<ISession?> GetOrCreateSession(TNode node, CancellationToken token)
    {
        if (_activeSessions.TryGetValue(node, out var existing))
            return existing;

        var multiaddress = GetMultiaddressForNode(node);
        if (multiaddress is null)
        {
            _logger.LogError("Cannot get multiaddress for node {Node}", node);
            return null;
        }

        try
        {
            var session = await _localPeer.DialAsync(multiaddress, token).ConfigureAwait(false);
            if (_activeSessions.TryAdd(node, session))
                return session;

            if (_activeSessions.TryGetValue(node, out var cached))
            {
                await SafeDisconnectAsync(session).ConfigureAwait(false);
                return cached;
            }
            return session;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create session to {Node}", node);
            return null;
        }
    }

    private static byte[] ConvertKeyToBytes(TPublicKey key) => key switch
    {
        byte[] bytes => bytes,
        ReadOnlyMemory<byte> rom => rom.ToArray(),
        // Send raw peer-ID bytes on the wire; the receiver hashes once to get the DHT key.
        // Sending Hash.Bytes here would cause a double-hash: SHA-256(SHA-256(peerId)).
        PublicKey kadPublicKey => kadPublicKey.Bytes.ToArray(),
        ValueHash256 valueHash => valueHash.Bytes,
        _ => Encoding.UTF8.GetBytes(key.ToString() ?? string.Empty)
    };

    private TNode? ConvertWirePeerToNode(Message.Types.Peer wirePeer)
    {
        if (typeof(TNode) != typeof(DhtNode)) return null;

        var dhtNode = MessageHelper.FromWirePeer(wirePeer);
        return dhtNode is not null ? (TNode)(object)dhtNode : default;
    }

    private Multiaddress? GetMultiaddressForNode(TNode node)
    {
        if (node is not DhtNode dhtNode) return null;

        foreach (var raw in dhtNode.Multiaddrs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try { return Multiaddress.Decode(raw); }
            catch { /* skip malformed */ }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var (_, session) in _activeSessions.ToArray())
            await SafeDisconnectAsync(session).ConfigureAwait(false);

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
        try { await session.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning("Error disposing session: {Error}", ex.Message); }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
