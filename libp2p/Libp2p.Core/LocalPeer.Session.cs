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
        public async Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
        {
            object? result = await DialAsyncCore(peer.GetProtocolInstance<TProtocol>(), request, token);
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
        private bool _disconnected;
        private bool _connectionCounted;
        private bool _sessionCounted;

        internal void RegisterConnection()
        {
            lock (_lifecycleLock)
            {
                if (_disconnected) throw new OperationCanceledException(ConnectionToken);
                if (_connectionCounted) return;
                _connectionCounted = true;
                Libp2pMetrics.ConnectionsOpened.Add(1);
                Libp2pMetrics.ConnectionsActive.Add(1);
            }
        }

        internal void RegisterSession()
        {
            lock (_lifecycleLock)
            {
                if (_disconnected) throw new OperationCanceledException(ConnectionToken);
                if (_sessionCounted) return;
                _sessionCounted = true;
                Libp2pMetrics.SessionsOpened.Add(1);
                Libp2pMetrics.SessionsActive.Add(1);
            }
        }

        public Task DisconnectAsync()
        {
            bool closeSession;
            bool closeConnection;
            lock (_lifecycleLock)
            {
                if (_disconnected) return Task.CompletedTask;
                _disconnected = true;
                closeSession = _sessionCounted;
                closeConnection = _connectionCounted;
            }

            try
            {
                connectionTokenSource.Cancel();
            }
            finally
            {
                if (closeSession)
                {
                    ConnectedTcs.TrySetCanceled(ConnectionToken);
                    Libp2pMetrics.SessionsClosed.Add(1);
                    Libp2pMetrics.SessionsActive.Add(-1);
                }
                if (closeConnection)
                {
                    Libp2pMetrics.ConnectionsActive.Add(-1);
                }
                peer.RemoveSession(this);
            }
            return Task.CompletedTask;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        public TaskCompletionSource ConnectedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Connected => ConnectedTcs.Task;

        internal IEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);
    }

    private void RemoveSession(Session session)
    {
        lock (Sessions)
        {
            Sessions.Remove(session);
        }
    }
}
