// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;
using System.Runtime.CompilerServices;

namespace Libp2p.Protocols.Pubsub.E2eTests;

[TestFixture]
public class OutboundRecoveryTests
{
    [TestCase("subscribe", false)]
    [TestCase("unsubscribe", false)]
    [TestCase("failed-announcement", false)]
    [TestCase("subscribe", true)]
    [TestCase("unsubscribe", true)]
    [TestCase("failed-announcement", true)]
    public async Task RegisteredFloodsubRecoversTheCurrentSubscriptionSnapshot(string scenario, bool automatic)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        Gate gate = new();
        PubsubSettings settings = new()
        {
            AutoConnect = automatic,
            MaxOutboundRpcEntriesPerPeer = 1,
            HeartbeatInterval = int.MaxValue,
            ReconnectionPeriod = 2_000,
        };
        await using ServiceProvider firstServices = new ServiceCollection()
            .AddLibp2p<FloodsubBuilder>(builder => ((ILibp2pPeerFactoryBuilder)builder).WithPubsub())
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new Nethermind.Libp2p.Core.TestsBase.TestContextLoggerFactory())
            .AddSingleton(settings)
            .BuildServiceProvider();
        await using ServiceProvider secondServices = new ServiceCollection()
            .AddLibp2p<FloodsubBuilder>(builder => ((ILibp2pPeerFactoryBuilder)builder).WithPubsub())
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(new Nethermind.Libp2p.Core.TestsBase.TestContextLoggerFactory())
            .AddSingleton(new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue })
            .BuildServiceProvider();
        await using ILocalPeer first = firstServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer second = secondServices.GetRequiredService<IPeerFactory>().Create();
        PubsubRouter firstRouter = firstServices.GetRequiredService<PubsubRouter>();
        PubsubRouter secondRouter = secondServices.GetRequiredService<PubsubRouter>();
        firstRouter.GetTopic("keep");
        secondRouter.GetTopic("keep");
        secondRouter.GetTopic("change");
        if (scenario != "subscribe") firstRouter.GetTopic("change");
        await first.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await second.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await secondRouter.StartAsync(second, deadline.Token);
        ISession session = await first.DialAsync(second.ListenAddresses.First(), deadline.Token);
        Assert.That(session.RemotePeerId, Is.EqualTo(second.Identity.PeerId));
        await firstRouter.StartAsync(first, deadline.Token);
        List<Task> streams = [];
        streams.Add(session.DialAsync<FloodsubProtocol>(deadline.Token));
        bool Membership(PubsubRouter router, string topic, PeerId peer)
        {
            lock (router) return ((IRoutingStateContainer)router).FloodsubPeers.TryGetValue(topic, out var peers) && peers.Contains(peer);
        }
        async Task Until(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
        {
            try { while (!condition()) await Task.Delay(10, deadline.Token); }
            catch (OperationCanceledException) { Assert.Fail($"Timed out waiting for {expression}; first: {firstRouter}; second: {secondRouter}"); }
        }
        try
        {
            await Until(() => Membership(secondRouter, "keep", first.Identity.PeerId) && Membership(firstRouter, "keep", second.Identity.PeerId));
            if (scenario != "subscribe") await Until(() => Membership(secondRouter, "change", first.Identity.PeerId));
            // Confirm the initial snapshot has left the bounded writer before arming the test gate.
            TaskCompletionSource initial = new(TaskCreationOptions.RunContinuationsAsynchronously);
            secondRouter.GetTopic("keep").OnMessage += (_, _) => initial.TrySetResult();
            firstRouter.Publish("keep", [1]);
            await initial.Task.WaitAsync(deadline.Token);
            await Drain(firstRouter).WaitAsync(deadline.Token);
            InstallGate(firstRouter, second.Identity.PeerId, gate);
            if (scenario == "failed-announcement")
            {
                firstRouter.Unsubscribe("change");
                await gate.Entered.Task.WaitAsync(deadline.Token);
                gate.Result.SetResult(IOResult.InternalError);
            }
            else
            {
                firstRouter.Publish("keep", [2]);
                await gate.Entered.Task.WaitAsync(deadline.Token);
                if (scenario == "subscribe") firstRouter.Subscribe("change");
                else firstRouter.Unsubscribe("change");
            }
            await gate.Stopped.Task.WaitAsync(deadline.Token);
            await Until(() => session.Closed.IsCancellationRequested);
            await Until(() => !Membership(secondRouter, "keep", first.Identity.PeerId) && !Membership(secondRouter, "change", first.Identity.PeerId));
            if (!automatic)
            {
                ISession replacement = await first.DialAsync(second.ListenAddresses.First(), deadline.Token);
                streams.Add(replacement.DialAsync<FloodsubProtocol>(deadline.Token));
            }
            await Until(() => Membership(secondRouter, "keep", first.Identity.PeerId) && Membership(firstRouter, "keep", second.Identity.PeerId));
            if (scenario == "subscribe") await Until(() => Membership(secondRouter, "change", first.Identity.PeerId));
            Assert.That(Membership(secondRouter, "change", first.Identity.PeerId), Is.EqualTo(scenario == "subscribe"));
            TaskCompletionSource<byte[]> toFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<byte[]> toSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
            firstRouter.GetTopic("keep").OnMessage += (_, data) => toFirst.TrySetResult(data);
            secondRouter.GetTopic("keep").OnMessage += (_, data) => toSecond.TrySetResult(data);
            await Drain(firstRouter).WaitAsync(deadline.Token);
            await Drain(secondRouter).WaitAsync(deadline.Token);
            firstRouter.Publish("keep", [3]);
            secondRouter.Publish("keep", [4]);
            Assert.That(await toFirst.Task.WaitAsync(deadline.Token), Is.EqualTo(new byte[] { 4 }));
            Assert.That(await toSecond.Task.WaitAsync(deadline.Token), Is.EqualTo(new byte[] { 3 }));
            if (scenario == "subscribe")
            {
                TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                firstRouter.GetTopic("change").OnMessage += (_, _) => received.TrySetResult();
                secondRouter.Publish("change", [5]);
                await received.Task.WaitAsync(deadline.Token);
            }
        }
        finally
        {
            gate.Result.TrySetResult(IOResult.Aborted);
            await firstRouter.DisposeAsync();
            await secondRouter.DisposeAsync();
            await Task.WhenAll(streams).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.That(streams.Where(stream => stream.IsFaulted), Is.Empty);
        }
    }

    [Test]
    public async Task RegisteredFloodsubKeepsHealthyControlAndExpiresTheStalledSession()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        DeadlineClock clock = new();
        ServiceProvider Services(PubsubSettings settings) => new ServiceCollection()
            .AddLibp2p<FloodsubBuilder>(builder => ((ILibp2pPeerFactoryBuilder)builder).WithPubsub())
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(settings).BuildServiceProvider();
        PubsubSettings Settings() => new() { AutoConnect = false, HeartbeatInterval = int.MaxValue };
        PubsubSettings bounded = Settings();
        bounded.MaxOutboundRpcEntries = 4; // Two optional frames, two entries for required control.
        bounded.OutboundRpcTimeout = 100;
        await using ServiceProvider sourceServices = Services(bounded), stalledServices = Services(Settings()), healthyServices = Services(Settings());
        await using ILocalPeer source = sourceServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer stalled = stalledServices.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer healthy = healthyServices.GetRequiredService<IPeerFactory>().Create();
        PubsubRouter sourceRouter = sourceServices.GetRequiredService<PubsubRouter>();
        PubsubRouter stalledRouter = stalledServices.GetRequiredService<PubsubRouter>();
        PubsubRouter healthyRouter = healthyServices.GetRequiredService<PubsubRouter>();
        sourceRouter.GetTopic("blocked");
        sourceRouter.GetTopic("healthy");
        stalledRouter.GetTopic("blocked");
        healthyRouter.GetTopic("healthy");
        bool Membership(PubsubRouter router, string topic, PeerId peer)
        {
            lock (router) return ((IRoutingStateContainer)router).FloodsubPeers.TryGetValue(topic, out var peers) && peers.Contains(peer);
        }
        async Task Until(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(1, deadline.Token);
        }
        foreach (ILocalPeer peer in new[] { source, stalled, healthy })
            await peer.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await sourceRouter.StartAsync(source, deadline.Token);
        await stalledRouter.StartAsync(stalled, deadline.Token);
        await healthyRouter.StartAsync(healthy, deadline.Token);
        List<Task> streams = [];
        Gate gate = new();
        try
        {
            ISession stalledSession = await source.DialAsync(stalled.ListenAddresses.First(), deadline.Token);
            streams.Add(stalledSession.DialAsync<FloodsubProtocol>(deadline.Token));
            await Until(() => Membership(sourceRouter, "blocked", stalled.Identity.PeerId) && Membership(stalledRouter, "blocked", source.Identity.PeerId));
            await Drain(sourceRouter).WaitAsync(deadline.Token);
            InstallGate(sourceRouter, stalled.Identity.PeerId, gate);
            sourceRouter.Publish("blocked", [1]);
            await gate.Entered.Task.WaitAsync(deadline.Token);
            sourceRouter.Publish("blocked", [2]);
            Assert.That(Usage(sourceRouter).Entries, Is.EqualTo(2));
            // A new registered stream must still advertise its current subscription snapshot.
            ISession healthySession = await source.DialAsync(healthy.ListenAddresses.First(), deadline.Token);
            streams.Add(healthySession.DialAsync<FloodsubProtocol>(deadline.Token));
            await Until(() => Membership(sourceRouter, "healthy", healthy.Identity.PeerId) && Membership(healthyRouter, "healthy", source.Identity.PeerId));
            await DrainPeer(sourceRouter, healthy.Identity.PeerId).WaitAsync(deadline.Token);
            Assert.That(gate.Stopped.Task.IsCompleted, Is.False);
            Assert.That(Usage(sourceRouter).Entries, Is.EqualTo(2));
            sourceRouter.Subscribe("change");
            await Until(() => Membership(healthyRouter, "change", source.Identity.PeerId));
            await DrainPeer(sourceRouter, healthy.Identity.PeerId).WaitAsync(deadline.Token);
            Assert.That(Usage(sourceRouter).Entries, Is.EqualTo(3));
            clock.Expire();
            await gate.Stopped.Task.WaitAsync(deadline.Token);
            await Until(() => stalledSession.Closed.IsCancellationRequested && Usage(sourceRouter).Entries == 0);
            await Until(() => !Membership(stalledRouter, "blocked", source.Identity.PeerId));
            Assert.That(healthySession.Closed.IsCancellationRequested, Is.False);
            Assert.That(Membership(healthyRouter, "healthy", source.Identity.PeerId), Is.True);
            sourceRouter.Unsubscribe("change");
            await Until(() => !Membership(healthyRouter, "change", source.Identity.PeerId));
            TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            healthyRouter.GetTopic("healthy").OnMessage += (_, data) => received.TrySetResult(data);
            sourceRouter.Publish("healthy", [3]);
            Assert.That(await received.Task.WaitAsync(deadline.Token), Is.EqualTo(new byte[] { 3 }));
            Assert.That(Usage(sourceRouter).Entries, Is.LessThanOrEqualTo(4));
        }
        finally
        {
            gate.Result.TrySetResult(IOResult.Aborted);
            await sourceRouter.DisposeAsync();
            await stalledRouter.DisposeAsync();
            await healthyRouter.DisposeAsync();
            await Task.WhenAll(streams).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Assert.That(streams.Where(stream => stream.IsFaulted), Is.Empty);
        }
    }

    private static object Peer(PubsubRouter router, PeerId peerId)
    {
        object peers = typeof(PubsubRouter).GetField("peerState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(router)!;
        return peers.GetType().GetProperty("Item")!.GetValue(peers, [peerId])!;
    }

    private static Task DrainPeer(PubsubRouter router, PeerId peerId)
    {
        object signal = Peer(router, peerId).GetType().GetField("Idle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Peer(router, peerId))!;
        return (Task)signal.GetType().GetProperty("Task")!.GetValue(signal)!;
    }

    private static (int Entries, long Bytes) Usage(PubsubRouter router) => ((int, long))typeof(PubsubRouter)
        .GetProperty("OutboundUsage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!;

    private sealed class DeadlineClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Expire()
        {
            Timer[] timers;
            lock (_timers)
            {
                _ticks += TimeSpan.FromMilliseconds(100).Ticks;
                timers = _timers.Where(timer => timer.Enabled).ToArray();
            }
            foreach (Timer timer in timers) timer.Fire();
        }
        private sealed class Timer(DeadlineClock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Enabled;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers) Enabled = dueTime != Timeout.InfiniteTimeSpan;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._timers) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private static Task Drain(PubsubRouter router) => (Task)typeof(PubsubRouter)
        .GetMethod("FlushOutboundAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(router, null)!;

    // Keep the standard TCP/Noise/Yamux stack and its WithPubsub registration, offering Floodsub only.
    public sealed class FloodsubBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] Pubsub() => [Get<FloodsubProtocol>()];
    }

    private sealed class Gate
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<IOResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Pause the registered writer after its initial snapshot. The delegate being replaced is the
    // production DialAsync channel write; the real stream, framing, teardown and session remain owned.
    private static void InstallGate(PubsubRouter router, PeerId peerId, Gate gate)
    {
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object peer = Peer(router, peerId);
        var write = peer.GetType().GetField("Write", fields)!;
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IOResult>> paused = async (_, token) =>
        {
            gate.Entered.TrySetResult();
            try { return await gate.Result.Task.WaitAsync(token); }
            finally { gate.Stopped.TrySetResult(); }
        };
        write.SetValue(peer, paused);
    }
}
