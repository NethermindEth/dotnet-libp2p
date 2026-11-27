// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.Pubsub;

public partial class PubsubRouter
{
    private const int MaxParallelReconnections = 5;
    private const int MaxPendingReconnections = 10_000;
    private readonly IPubsubReconnectPolicy? _reconnectPolicy;
    private readonly Lock _reconnectDispatchLock = new();
    private readonly Channel<bool> _reconnectSignal = System.Threading.Channels.Channel.CreateBounded<bool>(1);
    private readonly LinkedList<Reconnection> _reconnections = [];
    private long _reconnectionOrder;
    private int _reconnectionCount;
    private int _activeReconnections;

    internal sealed class ReconnectionState(PeerId peerId)
    {
        public PeerId PeerId { get; } = peerId;
        public volatile bool Suppressed;
        public Multiaddress? LastOutboundAddress;
        public Reconnection? Retry;
        public ConnectionAttempt? Connecting;
        public bool RetryEnded;
        public int Disconnecting;
    }

    internal sealed class Reconnection
    {
        public Reconnection(ReconnectionState state, PubsubReconnectReason reason, Multiaddress[]? discoveredAddresses)
        {
            State = state;
            Reason = reason;
            DiscoveredAddresses = discoveredAddresses;
            Node = new(this);
        }

        public ReconnectionState State { get; }
        public PubsubReconnectReason Reason { get; }
        public Multiaddress[]? DiscoveredAddresses { get; }
        public LinkedListNode<Reconnection> Node { get; }
        public long QueuedAt;
        public TimeSpan? Delay;
        public long Order;
        public int Attempts;
    }

    internal sealed class ConnectionAttempt(ReconnectionState state, Reconnection? retry, Multiaddress[]? discoveredAddresses)
    {
        public ReconnectionState State { get; } = state;
        public Reconnection? Retry { get; } = retry;
        public Multiaddress[]? DiscoveredAddresses { get; } = discoveredAddresses;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Superseded;
    }

    private bool IsStopping => _stopped.IsCancellationRequested || _lifetimeToken.IsCancellationRequested;

    private bool NeedsConnection(ReconnectionState state) => !IsStopping && !state.Suppressed &&
        (!peerState.TryGetValue(state.PeerId, out PubsubPeer? peer) || !peer.HasWriter);

    private bool IsConnectionPending(ReconnectionState state) =>
        state.Connecting is not null || IsOutboundBlocked(state);

    private bool IsOutboundBlocked(ReconnectionState state) =>
        state.Disconnecting != 0 || outboundDials.ContainsKey(state.PeerId) ||
        peerState.TryGetValue(state.PeerId, out PubsubPeer? peer) && peer.Terminal;

    private void SignalReconnect() => _reconnectSignal.Writer.TryWrite(true);

    // Candidate membership, establishment slots and outbound reservations share the routing lock.
    private void QueueReconnection(ReconnectionState state, PubsubReconnectReason reason, Multiaddress[]? discoveredAddresses = null)
    {
        if (!_settings.AutoConnect || _settings.ReconnectionAttempts == 0 || state.Retry is not null || state.RetryEnded ||
            _reconnectionCount >= MaxPendingReconnections || state.PeerId == localPeer?.Identity.PeerId ||
            IsDirectPeer(state.PeerId) || !NeedsConnection(state)) return;

        Reconnection retry = new(state, reason, discoveredAddresses);
        state.Retry = retry;
        _reconnectionCount++;
        QueueNextAttempt(retry);
    }

    private void QueueNextAttempt(Reconnection retry)
    {
        retry.QueuedAt = _timeProvider.GetTimestamp();
        retry.Delay = null;
        EnqueueRetry(retry);
        SignalReconnect();
    }

    private void EnqueueRetry(Reconnection retry)
    {
        retry.Order = ++_reconnectionOrder;
        _reconnections.AddLast(retry.Node);
    }

    private void EndReconnection(Reconnection retry)
    {
        if (!ReferenceEquals(retry.State.Retry, retry)) return;
        if (retry.Node.List is not null) _reconnections.Remove(retry.Node);
        retry.State.Retry = null;
        retry.State.RetryEnded = true;
        _reconnectionCount--;
    }

    private void ClearReconnections()
    {
        while (_reconnections.First is { } first) EndReconnection(first.Value);
    }

    private async Task LoopReconnect(CancellationToken token)
    {
        using ITimer timer = _timeProvider.CreateTimer(_ => SignalReconnect(), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _reconnectSignal.Reader.ReadAsync(token).ConfigureAwait(false);
                TimeSpan next = DispatchReconnects(token, ignoreDelay: false);
                if (next == TimeSpan.Zero) SignalReconnect();
                timer.Change(next == TimeSpan.Zero ? Timeout.InfiniteTimeSpan : next, Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            lock (this) ClearReconnections();
        }
    }

    // Steps the same dispatcher without waiting for its clock in router integration tests.
    internal void Reconnect(CancellationToken token) => DispatchReconnects(token, ignoreDelay: true);

    private TimeSpan DispatchReconnects(CancellationToken token, bool ignoreDelay)
    {
        lock (_reconnectDispatchLock)
        {
            Reconnection? next = null;
            long batch;
            lock (this) batch = _reconnectionOrder;
            while (!token.IsCancellationRequested && !IsStopping)
            {
                Reconnection retry;
                Multiaddress? fallback;
                lock (this)
                {
                    if (_activeReconnections >= MaxParallelReconnections || _reconnections.First is not { } first ||
                        first.Value.Order > batch) break;
                    retry = first.Value;
                    _reconnections.RemoveFirst();
                    if (!NeedsConnection(retry.State))
                    {
                        EndReconnection(retry);
                        continue;
                    }
                    if (IsConnectionPending(retry.State))
                    {
                        EnqueueRetry(retry);
                        continue;
                    }
                    if (retry.Attempts >= _settings.ReconnectionAttempts)
                    {
                        EndReconnection(retry);
                        continue;
                    }
                    fallback = retry.State.LastOutboundAddress;
                }

                TimeSpan? delay = retry.Delay ?? GetRetryDelay(retry);
                TimeSpan remaining = delay.GetValueOrDefault() - _timeProvider.GetElapsedTime(retry.QueuedAt);
                Multiaddress[]? addresses = null;
                ISession? existing = null;
                bool due = ignoreDelay || remaining <= TimeSpan.Zero;
                if (delay.HasValue && due && !token.IsCancellationRequested && !IsStopping)
                {
                    try
                    {
                        addresses = GetRetryAddresses(retry, fallback);
                        if (addresses is null)
                            existing = localPeer?.Sessions.FirstOrDefault(session => session.IsInitialized &&
                                !session.Closed.IsCancellationRequested && session.RemotePeerId == retry.State.PeerId);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(ex, "Could not resolve an address for pubsub peer {PeerId}", retry.State.PeerId);
                    }
                }

                ConnectionAttempt work;
                lock (this)
                {
                    if (!ReferenceEquals(retry.State.Retry, retry)) continue;
                    if (delay is null || !NeedsConnection(retry.State))
                    {
                        EndReconnection(retry);
                        continue;
                    }
                    retry.Delay = delay;
                    if (token.IsCancellationRequested || IsConnectionPending(retry.State))
                    {
                        EnqueueRetry(retry);
                        continue;
                    }
                    if (!due)
                    {
                        if (next is null || remaining < RemainingDelay(next)) next = retry;
                        EnqueueRetry(retry);
                        continue;
                    }
                    if (addresses is null && existing is null)
                    {
                        EndReconnection(retry);
                        continue;
                    }

                    retry.Attempts++;
                    _activeReconnections++;
                    work = BeginConnection(retry.State, retry: retry);
                }
                _ = Connect(work, addresses, _lifetimeToken, existing);
            }
            if (next is null) return Timeout.InfiniteTimeSpan;
            TimeSpan untilNext = RemainingDelay(next);
            return untilNext > TimeSpan.Zero ? untilNext : TimeSpan.Zero;
        }
    }

    private TimeSpan RemainingDelay(Reconnection retry) => retry.Delay!.Value - _timeProvider.GetElapsedTime(retry.QueuedAt);

    private Multiaddress[]? GetRetryAddresses(Reconnection retry, Multiaddress? fallback)
    {
        PeerId peerId = retry.State.PeerId;
        Multiaddress[] addresses = _peerStore.TryGetPeerInfo(peerId, out PeerMetadata? metadata) && metadata.Addresses is { } known
            ? [.. known.Where(address => address.GetPeerId() == peerId)] : [];
        if (addresses.Length == 0 && retry.DiscoveredAddresses is { } discovered)
            addresses = [.. discovered.Where(address => address.GetPeerId() == peerId)];
        if (fallback is not null && !addresses.Contains(fallback)) return [.. addresses, fallback];
        return addresses.Length == 0 ? null : addresses;
    }

    private TimeSpan? GetRetryDelay(Reconnection retry)
    {
        if (_reconnectPolicy is null)
            return _settings.ReconnectionPeriod == Timeout.Infinite ? null : TimeSpan.FromMilliseconds(_settings.ReconnectionPeriod);

        try
        {
            TimeSpan? delay = _reconnectPolicy.GetRetryDelay(new(retry.State.PeerId, retry.Reason, retry.Attempts + 1));
            if (delay is { } value && (value < TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(delay), "The retry delay must fit in a non-negative 32-bit millisecond interval.");
            return delay;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Pubsub reconnect policy failed for {PeerId}", retry.State.PeerId);
            return null;
        }
    }

    private ConnectionAttempt BeginConnection(ReconnectionState state, Reconnection? retry = null, Multiaddress[]? discoveredAddresses = null)
    {
        ConnectionAttempt work = new(state, retry, discoveredAddresses);
        state.Connecting = work;
        Track(work.Completion.Task);
        return work;
    }

    private void ConnectDiscoveredPeer(Multiaddress[] addresses, CancellationToken token)
    {
        PeerId? peerId = addresses.Select(address => address.GetPeerId()).FirstOrDefault(id => id is not null);
        if (peerId is null || peerId == localPeer?.Identity.PeerId || IsDirectPeer(peerId)) return;

        ConnectionAttempt work;
        lock (this)
        {
            if (IsStopping) return;
            ReconnectionState state = GetReconnectionState(peerId);
            // Address updates become visible through the store on the next attempt. They do not
            // compete with an existing attempt or restart its budget.
            if (state.Disconnecting != 0 || state.Connecting is not null || state.Retry is not null || !NeedsConnection(state)) return;
            state.RetryEnded = false;
            work = BeginConnection(state, discoveredAddresses: addresses);
        }
        _ = Connect(work, addresses, token);
    }

    private async Task Connect(ConnectionAttempt work, Multiaddress[]? addresses, CancellationToken token, ISession? existing = null)
    {
        ReconnectionState state = work.State;
        OutboundDialAttempt? outbound = null;
        bool failed = false;
        bool deferred = false;
        try
        {
            if (token.IsCancellationRequested || IsStopping || state.Suppressed || work.Superseded) return;
            ISession session = existing ?? await localPeer!.DialAsync(addresses!, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || state.Suppressed || work.Superseded || session.RemotePeerId != state.PeerId) return;

            IReadOnlyList<string> protocols = _peerStore.TryGetPeerInfo(state.PeerId, out PeerMetadata? metadata) ? metadata.SupportedProtocols ?? [] : [];
            Func<CancellationToken, Task>? dial = protocols.Contains(GossipsubProtocolVersionV13) ? session.DialAsync<GossipsubProtocolV13>
                : protocols.Contains(GossipsubProtocolVersionV12) ? session.DialAsync<GossipsubProtocolV12>
                : protocols.Contains(GossipsubProtocolVersionV11) ? session.DialAsync<GossipsubProtocolV11>
                : protocols.Contains(GossipsubProtocolVersionV10) ? session.DialAsync<GossipsubProtocol>
                : protocols.Contains(FloodsubProtocolVersion) ? session.DialAsync<FloodsubProtocol> : null;
            if (dial is null) return;

            lock (this)
            {
                if (work.Superseded || !NeedsConnection(state)) return;
                outbound = state.Disconnecting == 0 ? ReserveOutbound(state.PeerId) : null;
                if (outbound is not null) outbound.Connection = work;
                else deferred = IsOutboundBlocked(state);
            }
            if (outbound is not null && !work.Superseded) await dial(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failed = true;
            if (!token.IsCancellationRequested && !state.Suppressed)
                logger?.LogDebug(ex, "Pubsub dial failed for {PeerId}", state.PeerId);
        }
        finally
        {
            try
            {
                if (outbound is not null) ReleaseOutbound(state.PeerId, outbound);
                lock (this)
                {
                    if (ReleaseConnection(work))
                        FinishConnection(work, failed, deferred);
                }
                work.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                work.Completion.TrySetException(ex);
            }
        }
    }

    private bool ReleaseConnection(ConnectionAttempt work)
    {
        if (!ReferenceEquals(work.State.Connecting, work)) return false;
        work.State.Connecting = null;
        if (work.Retry is not null) _activeReconnections--;
        SignalReconnect();
        return true;
    }

    private void FinishConnection(ConnectionAttempt work, bool failed, bool deferred)
    {
        if (work.Superseded) return;
        ReconnectionState state = work.State;
        if (work.Retry is { } retry && ReferenceEquals(state.Retry, retry))
        {
            if (deferred && NeedsConnection(state))
            {
                // Keep the budget when a competing stream or teardown prevented negotiation.
                retry.Attempts--;
                EnqueueRetry(retry);
            }
            else if (failed && NeedsConnection(state) &&
                (retry.Attempts < _settings.ReconnectionAttempts || outboundDials.ContainsKey(state.PeerId)))
                QueueNextAttempt(retry);
            else
                EndReconnection(retry);
        }
        else if (failed && work.DiscoveredAddresses is { } addresses)
            QueueReconnection(state, PubsubReconnectReason.DiscoveryDialFailed, addresses);
    }
}
