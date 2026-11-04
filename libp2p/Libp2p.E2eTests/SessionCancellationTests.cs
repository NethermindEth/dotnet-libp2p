// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net.Quic;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class SessionCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [TestCase("yamux")]
    [TestCase("quic")]
    public async Task PreCanceledDialNeverInvokesEitherProtocol(string transport)
    {
        RequireTransport(transport);
        await using CancellationSetup setup = new(transport);
        await setup.ConnectAsync();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await setup.Outgoing.DialAsync<ControlledProtocol, int, int>(1, new(true)));
        // A successful following stream is a barrier through the same muxer request queue.
        Assert.That(await setup.Outgoing.DialAsync<IncrementNumberTestProtocol, int, int>(41).WaitAsync(Timeout), Is.EqualTo(42));
        Assert.That(setup.Dialer.Calls, Is.Zero);
        Assert.That(setup.Listener.Calls, Is.Zero);
    }

    [TestCase("yamux")]
    [TestCase("quic")]
    public async Task CancelingARunningInvocationLeavesSiblingStreamsUsable(string transport)
    {
        RequireTransport(transport);
        await using CancellationSetup setup = new(transport);
        await setup.ConnectAsync();
        using CancellationTokenSource caller = new();
        Task<int> canceledDial = setup.Outgoing.DialAsync<ControlledProtocol, int, int>(1, caller.Token);
        Task<int>? siblingDial = null;
        try
        {
            Invocation firstOutgoing = await setup.Dialer.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Invocation firstIncoming = await setup.Listener.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            siblingDial = setup.Outgoing.DialAsync<ControlledProtocol, int, int>(2);
            Invocation siblingOutgoing = await setup.Dialer.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Invocation siblingIncoming = await setup.Listener.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

            caller.Cancel();

            Assert.CatchAsync<OperationCanceledException>(async () => await canceledDial.WaitAsync(Timeout));
            await firstOutgoing.Canceled.Task.WaitAsync(Timeout);
            Assert.That(firstOutgoing.Token.IsCancellationRequested, Is.True);
            Assert.That(firstIncoming.Token.IsCancellationRequested, Is.False);
            Assert.That(siblingOutgoing.Token.IsCancellationRequested, Is.False);
            Assert.That(siblingIncoming.Token.IsCancellationRequested, Is.False);
            Assert.That(siblingDial.IsCompleted, Is.False);
            Assert.That(setup.Outgoing.Closed.IsCancellationRequested, Is.False);
            Assert.That(setup.Incoming.Closed.IsCancellationRequested, Is.False);

            firstIncoming.Release.TrySetResult();
            siblingOutgoing.Release.TrySetResult();
            siblingIncoming.Release.TrySetResult();
            Assert.That(await siblingDial.WaitAsync(Timeout), Is.EqualTo(3));
            Assert.That(await setup.Incoming.DialAsync<IncrementNumberTestProtocol, int, int>(51).WaitAsync(Timeout), Is.EqualTo(52));
        }
        finally
        {
            caller.Cancel();
            setup.ReleaseAll();
            await setup.Outgoing.DisconnectAsync();
            await ((Task)canceledDial).WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (siblingDial is not null) await ((Task)siblingDial).WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("yamux", false)]
    [TestCase("yamux", true)]
    [TestCase("quic", false)]
    [TestCase("quic", true)]
    public async Task DisconnectSignalsRunningInvocationsInBothDirections(string transport, bool listenerDisconnects)
    {
        RequireTransport(transport);
        await using CancellationSetup setup = new(transport);
        await setup.ConnectAsync();
        Task<int> dial = setup.Outgoing.DialAsync<ControlledProtocol, int, int>(1);
        try
        {
            Invocation outgoing = await setup.Dialer.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Invocation incoming = await setup.Listener.Started.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.That(outgoing.Token.CanBeCanceled, Is.True);
            Assert.That(incoming.Token.CanBeCanceled, Is.True);

            await (listenerDisconnects ? setup.Incoming : setup.Outgoing).DisconnectAsync();

            await Task.WhenAll(outgoing.Canceled.Task, incoming.Canceled.Task).WaitAsync(Timeout);
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));
            Assert.That(outgoing.Token.IsCancellationRequested, Is.True);
            Assert.That(incoming.Token.IsCancellationRequested, Is.True);
            Assert.That(setup.Dialer.Calls, Is.EqualTo(1));
            Assert.That(setup.Listener.Calls, Is.EqualTo(1));
        }
        finally
        {
            setup.ReleaseAll();
            await setup.Outgoing.DisconnectAsync();
            await ((Task)dial).WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static void RequireTransport(string transport)
    {
        if (transport == "quic" && !QuicListener.IsSupported)
            Assert.Inconclusive("QUIC is not supported in this environment.");
    }

    private sealed class CancellationSetup(string transport) : E2eTestSetup
    {
        public ISession Outgoing { get; private set; } = null!;
        public ISession Incoming { get; private set; } = null!;
        public ControlledProtocol Dialer => ServiceProviders[0].GetRequiredService<ControlledProtocol>();
        public ControlledProtocol Listener => ServiceProviders[1].GetRequiredService<ControlledProtocol>();

        protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
        {
            if (transport == "quic") builder.WithQuic();
            return builder.AddProtocol<ControlledProtocol>().AddProtocol<IncrementNumberTestProtocol>();
        }

        protected override IServiceCollection ConfigureServices(IServiceCollection services)
            => base.ConfigureServices(services).AddSingleton<ControlledProtocol>();

        protected override Multiaddress[] GetListenAddresses(int index)
            => [transport == "quic" ? "/ip4/127.0.0.1/udp/0/quic-v1" : "/ip4/127.0.0.1/tcp/0"];

        public async Task ConnectAsync()
        {
            await AddPeersAsync(2);
            TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Peers[1].OnConnected += session => accepted.TrySetResult(session);
            Outgoing = await Peers[0].DialAsync([.. Peers[1].ListenAddresses]).WaitAsync(Timeout);
            Incoming = await accepted.Task.WaitAsync(Timeout);
        }

        public void ReleaseAll()
        {
            Dialer.ReleaseAll();
            Listener.ReleaseAll();
        }
    }

    public sealed class ControlledProtocol : ISessionProtocol<int, int>
    {
        private readonly ConcurrentBag<Invocation> _invocations = [];
        private int _calls;
        public string Id => "/test/controlled-cancellation/1.0.0";
        public int Calls => Volatile.Read(ref _calls);
        public System.Threading.Channels.Channel<Invocation> Started { get; } = System.Threading.Channels.Channel.CreateUnbounded<Invocation>();

        public async Task<int> DialAsync(IChannel channel, ISessionContext context, int request)
        {
            await channel.WriteVarintAsync(request);
            await WaitAsync(context);
            return await channel.ReadVarintAsync(context.UpgradeOptions!.CancellationToken);
        }

        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            int request = await channel.ReadVarintAsync(context.UpgradeOptions?.CancellationToken ?? default);
            await WaitAsync(context);
            await channel.WriteVarintAsync(request + 1);
        }

        private async Task WaitAsync(ISessionContext context)
        {
            Interlocked.Increment(ref _calls);
            Invocation invocation = new(context.UpgradeOptions?.CancellationToken ?? default);
            _invocations.Add(invocation);
            Started.Writer.TryWrite(invocation);
            try { await invocation.Release.Task.WaitAsync(invocation.Token); }
            catch (OperationCanceledException) when (invocation.Token.IsCancellationRequested)
            {
                invocation.Canceled.TrySetResult();
                throw;
            }
        }

        public void ReleaseAll()
        {
            foreach (Invocation invocation in _invocations) invocation.Release.TrySetResult();
        }
    }

    public sealed class Invocation(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
