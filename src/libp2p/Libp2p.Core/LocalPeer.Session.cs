// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Metrics;
using Nethermind.Libp2p.Core.Dto;
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
        public PublicKey? RemotePublicKey => State.RemotePublicKey;

        private readonly System.Threading.Channels.Channel<UpgradeOptions> SubDialRequests =
            System.Threading.Channels.Channel.CreateUnbounded<UpgradeOptions>();

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

        public Task<IChannel> OpenStreamAsync<TProtocol>(CancellationToken token = default) where TProtocol : ISessionListenerProtocol
        {
            return OpenStreamAsyncCore(peer.GetProtocolInstance<TProtocol>(), token);
        }

        public Task<IChannel> OpenStreamAsync(ISessionListenerProtocol protocol, CancellationToken token = default)
        {
            return OpenStreamAsyncCore(protocol, token);
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

            TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => tcs.TrySetCanceled(token));

            await SubDialRequests.Writer.WriteAsync(new UpgradeOptions()
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

        private async Task<IChannel> OpenStreamAsyncCore(IProtocol? protocol, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (protocol is null)
            {
                throw new Libp2pSetupException("Protocol is not added.");
            }

            TaskCompletionSource<object?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = token.Register(() => tcs.TrySetCanceled(token));

            await SubDialRequests.Writer.WriteAsync(new UpgradeOptions()
            {
                CompletionSource = tcs,
                SelectedProtocol = protocol,
                StopAfterProtocolSelection = true,
                CancellationToken = token
            }, token);

            object? result = await tcs.Task;
            MarkAsConnected();
            return (IChannel)result!;
        }


        private CancellationTokenSource connectionTokenSource = new();

        public Task DisconnectAsync()
        {
            SubDialRequests.Writer.TryComplete();
            connectionTokenSource.Cancel();
            peer.RemoveSession(this);
            return Task.CompletedTask;
        }

        public CancellationToken ConnectionToken => connectionTokenSource.Token;


        public TaskCompletionSource ConnectedTcs = new();
        public Task Connected => ConnectedTcs.Task;

        internal void MarkAsConnected() => ConnectedTcs?.TrySetResult();

        internal IAsyncEnumerable<UpgradeOptions> GetRequestQueue() => SubDialRequests.Reader.ReadAllAsync(ConnectionToken);
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
