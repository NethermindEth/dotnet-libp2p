// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using System.Collections.Concurrent;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Integration;

public class LibP2pKademliaMessageSender : IDhtMessageSender
{
    private readonly ILocalPeer _localPeer;
    private readonly KadDhtOptions _options;
    private readonly ILogger<LibP2pKademliaMessageSender>? _logger;
    private readonly TimeSpan _operationTimeout;
    private readonly Action<DhtNode>? _onPeerDiscovered;
    private readonly Action<DhtNode>? _onPingResponse;
    /// <summary>Liveness checks in flight, one per peer, so overlapping checks share an answer.</summary>
    private readonly ConcurrentDictionary<PeerId, Task<bool>> _livenessProbes = new();

    public LibP2pKademliaMessageSender(
        ILocalPeer localPeer,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? operationTimeout = null,
        Action<DhtNode>? onPeerDiscovered = null,
        Action<DhtNode>? onPingResponse = null)
        : this(new KadDhtOptions(), localPeer, loggerFactory, operationTimeout, onPeerDiscovered, onPingResponse)
    {
    }

    public LibP2pKademliaMessageSender(
        KadDhtOptions options,
        ILocalPeer localPeer,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? operationTimeout = null,
        Action<DhtNode>? onPeerDiscovered = null,
        Action<DhtNode>? onPingResponse = null)
    {
        WirePeerPolicy.Validate(options);
        _options = options;
        _localPeer = localPeer ?? throw new ArgumentNullException(nameof(localPeer));
        _logger = loggerFactory?.CreateLogger<LibP2pKademliaMessageSender>();
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(30);
        _onPeerDiscovered = onPeerDiscovered;
        _onPingResponse = onPingResponse;
    }

    /// <summary>
    /// Checks whether <paramref name="receiver"/> answers a ping.
    /// </summary>
    /// <remarks>
    /// The ping protocol keeps one outbound stream per peer, so overlapping checks for the same peer
    /// share the probe that is already running instead of racing it. Without that, the loser's refusal
    /// would reach the health tracker as a failed request and evict a peer that is answering: the
    /// tracker removes a node whose refresh is still pending, both when the probe reports false and
    /// when it throws. Sharing the answer also keeps one caller's cancellation from ending a probe the
    /// others are still waiting on, so the probe runs on the operation timeout alone.
    /// </remarks>
    public async Task<bool> Ping(DhtNode receiver, CancellationToken token = default)
    {
        // A misconfigured stack is a setup error, not a dead peer, so it is thrown rather than reported as false.
        LivenessProbe.EnsureSupported(_localPeer);
        // A probe outlives the caller that started it, so a caller that has already given up must not
        // start one: it would dial, and on timeout disconnect the session, for nobody.
        token.ThrowIfCancellationRequested();
        PeerId peerId = receiver.PeerId;

        while (true)
        {
            if (_livenessProbes.TryGetValue(peerId, out Task<bool>? running))
            {
                _logger?.LogTrace("Joining the liveness check already running for {NodeId}", peerId);
                return await running.WaitAsync(token).ConfigureAwait(false);
            }

            TaskCompletionSource<bool> probe = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Lost the race to publish; join the winner rather than open a second stream.
            if (!_livenessProbes.TryAdd(peerId, probe.Task)) continue;

            _ = RunLivenessProbeAsync(receiver, peerId, probe);
            return await probe.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the shared probe, completes its waiters, and retires the entry it published.</summary>
    private async Task RunLivenessProbeAsync(DhtNode receiver, PeerId peerId, TaskCompletionSource<bool> probe)
    {
        try
        {
            probe.TrySetResult(await ProbeLivenessAsync(receiver).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            probe.TrySetException(ex);
        }
        finally
        {
            // Keyed on the published task as well, so a probe that has already been replaced
            // cannot retire its successor.
            _livenessProbes.TryRemove(new KeyValuePair<PeerId, Task<bool>>(peerId, probe.Task));
        }
    }

    private async Task<bool> ProbeLivenessAsync(DhtNode receiver)
    {
        _logger?.LogDebug("Checking liveness of {NodeId}", receiver.PeerId);

        try
        {
            // The probe is shared, so no one caller owns its lifetime; the operation timeout bounds it.
            using var timeoutCts = new CancellationTokenSource(_operationTimeout);

            // Kad-DHT PING is deprecated and must not be sent; probe with the ping protocol instead.
            // A successful dial alone can return a cached session without touching the wire.
            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            // Coalescing keeps our own checks off the contended path, so a refusal here means a ping
            // from elsewhere in the process holds the stream.
            await LivenessProbe.ProbeWaitingForStreamAsync(session, _operationTimeout, CancellationToken.None, timeoutCts.Token);

            _logger?.LogTrace("Connection to {NodeId} is alive", receiver.PeerId);
            // Bootstrap relies on the transport to re-admit responding bootnodes.
            _onPingResponse?.Invoke(receiver);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Ping to {NodeId} failed", receiver.PeerId);
            return false;
        }
    }

    public async Task<DhtNode[]?> FindNeighbours(DhtNode receiver, PublicKey target, CancellationToken token = default)
    {
        _logger?.LogDebug("Sending FindNeighbours to {NodeId}", receiver.PeerId);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateFindNodeRequest(target.Bytes.ToArray());
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            var nodes = ParseCloserPeers(response);
            _logger?.LogDebug("Received {Count} neighbours from {NodeId}", nodes.Length, receiver.PeerId);
            return nodes;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("FindNeighbours to {NodeId} cancelled", receiver.PeerId);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger?.LogWarning(ex, "FindNeighbours to {NodeId} timed out", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "FindNeighbours to {NodeId} failed", receiver.PeerId);
            throw;
        }
    }

    public async Task<bool> PutValueAsync(DhtNode receiver, byte[] key, byte[] value, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreatePutValueRequest(key, value);
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            // Per spec: verify the echoed record matches what was sent
            if (response.Record is null)
                return false;

            if (!response.Record.Key.Span.SequenceEqual(key))
            {
                _logger?.LogWarning("PutValue to {NodeId}: echoed key mismatch", receiver.PeerId);
                return false;
            }

            if (!response.Record.Value.Span.SequenceEqual(value))
            {
                _logger?.LogWarning("PutValue to {NodeId}: echoed value mismatch", receiver.PeerId);
                return false;
            }

            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("PutValue to {NodeId} cancelled", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "PutValue to {NodeId} failed", receiver.PeerId);
            return false;
        }
    }

    public async Task<GetValueResult> GetValueAsync(DhtNode receiver, byte[] key, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateGetValueRequest(key);
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            byte[]? value = null;
            long timestamp = 0;
            if (response.Record is { Value.IsEmpty: false } && response.Record.Key.Span.SequenceEqual(key))
            {
                value = response.Record.Value.ToByteArray();
                if (response.Record.HasTimeReceived && DateTimeOffset.TryParse(response.Record.TimeReceived, out var dto))
                    timestamp = dto.ToUnixTimeSeconds();
            }

            return new GetValueResult
            {
                Value = value,
                Timestamp = timestamp,
                CloserPeers = ParseCloserPeers(response)
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("GetValue from {NodeId} cancelled", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetValue from {NodeId} failed", receiver.PeerId);
            throw;
        }
    }

    public async Task AddProviderAsync(DhtNode receiver, byte[] key, DhtNode provider, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateAddProviderRequest(key, new[] { provider });
            await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("AddProvider to {NodeId} cancelled", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "AddProvider to {NodeId} failed", receiver.PeerId);
            throw;
        }
    }

    public async Task<GetProvidersResult> GetProvidersAsync(DhtNode receiver, byte[] key, CancellationToken token = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutCts.CancelAfter(_operationTimeout);

            var session = await DialNodeAsync(receiver, timeoutCts.Token);
            var request = MessageHelper.CreateGetProvidersRequest(key);
            var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, timeoutCts.Token);

            var peers = new WirePeerPolicy(_options).ReadResponse(response, _onPeerDiscovered);
            return new GetProvidersResult { Providers = peers.Providers, CloserPeers = peers.Closer };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("GetProviders from {NodeId} cancelled", receiver.PeerId);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "GetProviders from {NodeId} failed", receiver.PeerId);
            throw;
        }
    }

    private DhtNode[] ParseCloserPeers(Message response)
    {
        return new WirePeerPolicy(_options).ReadResponse(response, _onPeerDiscovered, includeProviders: false).Closer;
    }

    private async Task<ISession> DialNodeAsync(DhtNode targetNode, CancellationToken cancellationToken)
    {
        foreach (var addrStr in targetNode.Multiaddrs)
        {
            if (string.IsNullOrWhiteSpace(addrStr)) continue;
            try
            {
                var address = Multiaddress.Decode(addrStr);
                return await _localPeer.DialAsync(address, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogTrace(ex, "Failed to dial {Address} for {NodeId}", addrStr, targetNode.PeerId);
            }
        }

        try
        {
            return await _localPeer.DialAsync(targetNode.PeerId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Unable to connect to node {targetNode.PeerId}", ex);
        }
    }
}
