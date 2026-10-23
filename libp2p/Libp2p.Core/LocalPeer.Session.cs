// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Metrics;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public partial class LocalPeer
{
    public class Session(LocalPeer peer, Activity? activity = null) : ISession
    {
        private static int SessionIdCounter;

        public string Id { get; } = Interlocked.Increment(ref SessionIdCounter).ToString();
        public State State { get; } = new();
        public Activity? Activity { get; } = activity;

        public Multiaddress RemoteAddress => State.RemoteAddress ?? throw new Libp2pException("Session contains uninitialized remote address.");
        public PeerId RemotePeerId => State.RemotePeerId ?? throw new Libp2pException("Session contains uninitialized remote peer id.");
        public CancellationToken Closed => ConnectionToken;

        private readonly BlockingCollection<UpgradeOptions> SubDialRequests = [];

        /// <inheritdoc />
        public async Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
        {
            await DialAsyncCore(peer.GetProtocolInstance<TProtocol>(), null, token);
        }

        /// <summary>
        /// Dials a specific protocol instance on this session.
        /// </summary>
        /// <param name="protocol">The protocol instance to negotiate over this session.</param>
        /// <param name="token">Cancellation token used while queueing the dial request.</param>
        /// <returns>A task that completes when the dial request has been handled.</returns>
        public async Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        {
            await DialAsyncCore(protocol, null, token);
        }

        /// <inheritdoc />
        public async Task<TResponse> DialAsync<TProtocol, TResponse>(CancellationToken token = default) where TProtocol : ISessionProtocol<TResponse>
        {
            object? result = await DialAsyncCore(peer.GetProtocolInstance<TProtocol>(), null, token);
            return (TResponse)result!;
        }

        /// <inheritdoc />
        public async Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
        {
            object? result = await DialAsyncCore(peer.GetProtocolInstance<TProtocol>(), request, token);
            return (TResponse)result!;
        }

        /// <summary>Dials a specific protocol instance with a request and returns its response.</summary>
        public async Task<TResponse> DialAsync<TRequest, TResponse>(ISessionProtocol<TRequest, TResponse> protocol, TRequest request, CancellationToken token = default)
        {
            object? result = await DialAsyncCore(protocol, request, token);
            return (TResponse)result!;
        }

        private async Task<object?> DialAsyncCore(IProtocol? protocol, object? argument, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            CancellationToken connectionToken = ConnectionToken;
            connectionToken.ThrowIfCancellationRequested();

            TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => tcs.TrySetCanceled(token));
            using CancellationTokenRegistration connectionRegistration = connectionToken.Register(() => tcs.TrySetCanceled(connectionToken));
            connectionToken.ThrowIfCancellationRequested();

            SubDialRequests.Add(new UpgradeOptions()
            {
                CompletionSource = tcs,
                SelectedProtocol = protocol,
                Argument = argument,
                CancellationToken = token
            }, token);

            object? result = await tcs.Task;
            return result;
        }


        private CancellationTokenSource connectionTokenSource = new();
        private readonly Lock _lifecycleLock = new();
        private TaskCompletionSource? _disconnection;
        private bool _connectionCounted;
        private bool _sessionCounted;
        private PeerId? _retainedPeerId;

        private ITimer? _deadline;
        private int _deadlineStage;
        private bool _deadlineExpired;
        private bool _initializationAcquired;
        private readonly HashSet<Task> _upgrades = [];

        internal void StartUpgradeDeadline()
        {
            lock (_lifecycleLock)
            {
                if (_deadlineStage != 0 || _disconnection is not null) return;
                SetDeadline(1, peer.ResourceLimits.InboundUpgradeTimeout);
            }
        }

        // Called with the lifecycle lock held. Stale callbacks cannot expire the next stage.
        private void SetDeadline(int stage, TimeSpan timeout)
        {
            _deadline?.Dispose();
            _deadlineStage = stage;
            _deadline = peer.Clock.CreateTimer(_ =>
            {
                lock (_lifecycleLock)
                {
                    if (_deadlineStage != stage || _disconnection is not null) return;
                    _deadlineExpired = true;
                }
                try { Libp2pMetrics.DeadlinesExpired.Add(1, new KeyValuePair<string, object?>("stage", stage == 1 ? "upgrade" : "initialization")); }
                finally { ObserveLateFailure(DisconnectAsync()); }
            }, null, timeout, Timeout.InfiniteTimeSpan);
        }

        internal bool TryBeginInitialization()
        {
            lock (_lifecycleLock)
            {
                if (_deadlineExpired || _disconnection is not null) throw new OperationCanceledException(ConnectionToken);
                if (_initializationAcquired) return true;
                if (Interlocked.Increment(ref peer._initializations) > peer.ResourceLimits.MaxConcurrentInitializations)
                {
                    Interlocked.Decrement(ref peer._initializations);
                    return false;
                }
                _initializationAcquired = true;
                SetDeadline(2, peer.ResourceLimits.InitializationTimeout);
                return true;
            }
        }

        internal void EndInitialization()
        {
            lock (_lifecycleLock)
            {
                if (!_initializationAcquired) return;
                _initializationAcquired = false;
                Interlocked.Decrement(ref peer._initializations);
            }
        }

        internal Task RunUpgrade(IChannel channel, Func<Task> run)
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool closed;
            lock (_lifecycleLock)
            {
                closed = _disconnection is not null || _deadlineExpired;
                if (!closed) _upgrades.Add(completion.Task);
            }
            if (closed) return RejectAsync();
            Task task;
            try { task = run(); }
            catch
            {
                lock (_lifecycleLock) _upgrades.Remove(completion.Task);
                completion.TrySetResult();
                throw;
            }
            _ = FinishAsync(task);
            return task;

            async Task RejectAsync()
            {
                await channel.AbortAsync();
                throw new ChannelClosedException();
            }

            async Task FinishAsync(Task work)
            {
                await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                lock (_lifecycleLock) _upgrades.Remove(completion.Task);
                completion.TrySetResult();
            }
        }

        internal async Task DrainUpgradesAsync()
        {
            while (true)
            {
                Task[] pending;
                lock (_lifecycleLock) pending = [.. _upgrades];
                if (pending.Length == 0) return;
                await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        internal void RegisterConnection()
        {
            lock (_lifecycleLock)
            {
                if (_deadlineExpired || _disconnection is not null) throw new OperationCanceledException(ConnectionToken);
                if (_connectionCounted) return;
                peer._connections.Add(this);
                _connectionCounted = true;
                Libp2pMetrics.ConnectionsOpened.Add(1);
                Libp2pMetrics.ConnectionsActive.Add(1);
            }
        }

        internal void RegisterSession()
        {
            lock (_lifecycleLock)
            {
                if (_deadlineExpired || _disconnection is not null) throw new OperationCanceledException(ConnectionToken);
                if (_sessionCounted) return;
                peer._connections.Add(this);
                PeerId remotePeerId = State.RemotePeerId ?? throw new Libp2pException("Session contains uninitialized remote peer id.");
                peer._peerStore?.Retain(remotePeerId);
                _retainedPeerId = remotePeerId;
                _sessionCounted = true;
                Libp2pMetrics.SessionsOpened.Add(1);
                Libp2pMetrics.SessionsActive.Add(1);
            }
        }

        internal bool TrySetConnected()
        {
            lock (peer.Sessions)
                lock (_lifecycleLock)
                {
                    if (peer._disposal is not null || _disconnection is not null || _deadlineExpired || DuplicateSessionFailure is not null) return false;
                    _deadlineStage = 3;
                    _deadline?.Dispose();
                    _deadline = null;
                    return ConnectedTcs.TrySetResult();
                }
        }

        public Task DisconnectAsync()
        {
            bool closeSession;
            bool closeConnection;
            TaskCompletionSource disconnection;
            lock (_lifecycleLock)
            {
                if (_disconnection is not null) return _disconnection.Task;
                _disconnection = disconnection = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _deadlineStage = 3;
                _deadline?.Dispose();
                _deadline = null;
                closeSession = _sessionCounted;
                closeConnection = _connectionCounted;
            }

            Exception? failure = null;
            try
            {
                try { connectionTokenSource.Cancel(); }
                finally
                {
                    ConnectedTcs.TrySetCanceled(ConnectionToken);
                    if (closeSession)
                    {
                        if (_retainedPeerId is { } peerId) peer._peerStore?.Release(peerId);
                        Libp2pMetrics.SessionsClosed.Add(1);
                        Libp2pMetrics.SessionsActive.Add(-1);
                    }
                    if (closeConnection)
                    {
                        Libp2pMetrics.ConnectionsActive.Add(-1);
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            peer.CompleteDisconnection(this, disconnection, failure);
            return disconnection.Task;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        internal bool IsListenerConnection { get; set; }
        internal bool IsListenerSession { get; set; }
        internal volatile SessionExistsException? DuplicateSessionFailure;
        internal volatile Session? Replacement;
        internal Session? Replaces;

        internal SessionExistsException? GetDuplicateFailure()
            => DuplicateSessionFailure ?? (Replacement is { } replacement && CanAwaitSession(replacement)
                ? new SessionExistsException(RemotePeerId) : null);

        public TaskCompletionSource ConnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Connected => ConnectedTcs.Task;

        internal IEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);
    }

    private void CompleteDisconnection(Session session, TaskCompletionSource completion, Exception? failure)
    {
        // Collection and metric callbacks can disconnect during admission. Finish removal
        // after the current notification returns, when ObservableCollection permits it.
        if (Monitor.IsEntered(Sessions)) _ = Task.Run(Finish);
        else Finish();

        void Finish()
        {
            lock (Sessions)
            {
                try { Sessions.Remove(session); }
                catch (Exception exception)
                {
                    failure = failure is null ? exception : new AggregateException(failure, exception);
                }
                _connections.Remove(session);
                if (session.Replaces is { } original && ReferenceEquals(original.Replacement, session)) original.Replacement = null;
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }
    }
}
