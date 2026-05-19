// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Metrics;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core;

public partial class LocalPeer
{
    public class Session(LocalPeer peer) : ISession
    {
        private static int SessionIdCounter;

        public string Id { get; } = Interlocked.Increment(ref SessionIdCounter).ToString();
        public State State { get; } = new();
        public Activity? Activity { get; }

        public Multiaddress RemoteAddress => State.RemoteAddress ?? throw new Libp2pException("Session contains uninitialized remote address.");

        private readonly BlockingCollection<UpgradeOptions> SubDialRequests = [];

        public async Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionProtocol
        {
            await DialAsyncCore(peer.GetProtocolInstance<TProtocol>(), null, token);
        }

        public async Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        {
            await DialAsyncCore(protocol, null, token);
        }

        public async Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
        {
            object? result = await DialAsyncCore(peer.GetProtocolInstance<TProtocol>(), request, token);
            return (TResponse)result!;
        }

        private async Task<object?> DialAsyncCore(IProtocol? protocol, object? argument, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => tcs.TrySetCanceled(token));

            SubDialRequests.Add(new UpgradeOptions()
            {
                CompletionSource = tcs,
                SelectedProtocol = protocol,
                Argument = argument,
                CancellationToken = token
            }, token);

            object? result = await tcs.Task;
            MarkAsConnected();
            return result;
        }


        private CancellationTokenSource connectionTokenSource = new();

        public Task DisconnectAsync()
        {
            connectionTokenSource.Cancel();
            peer.RemoveSession(this);
            return Task.CompletedTask;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        public TaskCompletionSource ConnectedTcs = new();
        public Task Connected => ConnectedTcs.Task;

        internal void MarkAsConnected() => ConnectedTcs?.TrySetResult();

        internal IEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);
    }

    private void RemoveSession(Session session)
    {
        lock (Sessions)
        {
            Sessions.Remove(session);
        }
        Libp2pMetrics.SessionsClosed.Add(1);
        Libp2pMetrics.SessionsActive.Add(-1);
        Libp2pMetrics.ConnectionsActive.Add(-1);
    }
}
