// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
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
            TaskCompletionSource<object?> tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs!, SelectedProtocol = peer.GetProtocolInstance<TProtocol>() }, token);
            await tcs.Task;
            MarkAsConnected();
        }

        public async Task DialAsync(ISessionProtocol protocol, CancellationToken token = default)
        {
            TaskCompletionSource<object?> tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs, SelectedProtocol = protocol }, token);
            await tcs.Task;
            MarkAsConnected();
        }

        public async Task<TResponse> DialAsync<TProtocol, TRequest, TResponse>(TRequest request, CancellationToken token = default) where TProtocol : ISessionProtocol<TRequest, TResponse>
        {
            TaskCompletionSource<object?> tcs = new();
            SubDialRequests.Add(new UpgradeOptions() { CompletionSource = tcs, SelectedProtocol = peer.GetProtocolInstance<TProtocol>(), Argument = request }, token);
            await tcs.Task;
            MarkAsConnected();
            return (TResponse)tcs.Task.Result!;
        }


        private CancellationTokenSource connectionTokenSource = new();

        public Task DisconnectAsync()
        {
            connectionTokenSource.Cancel();
            peer.Sessions.Remove(this);
            return Task.CompletedTask;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        public TaskCompletionSource ConnectedTcs = new();
        public Task Connected => ConnectedTcs.Task;

        internal void MarkAsConnected() => ConnectedTcs?.TrySetResult();

        internal IEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.GetConsumingEnumerable(ConnectionToken);
    }
}
