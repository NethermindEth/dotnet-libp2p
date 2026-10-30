// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class OutboundDeadlineTests
{
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;
    private static PubsubSettings Settings() => new() { AutoConnect = false, OutboundRpcTimeout = 100, HeartbeatInterval = int.MaxValue };

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExpiryClosesTheStalledOwnerIncludingBeforeStreamAttachment(bool attached)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        OutboundWorkTests.PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer;
        if (attached) peer = OutboundWorkTests.Open(router, writer);
        else
        {
            router.InboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV11,
                new TaskCompletionSource().Task, () => Task.CompletedTask, writer.Disconnect);
            peer = Peer(router);
        }
        peer.Send(OutboundWorkTests.Iwant());
        if (attached) await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        Assert.That(router.OutboundUsage.Entries, Is.EqualTo(1));
        // Settings are a construction-time snapshot. Heartbeat is deliberately not running.
        settings.OutboundRpcTimeout = int.MaxValue;
        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.That(peer.Terminal, Is.False);
        Assert.That(peer.Send(new()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(router.OutboundUsage.Entries, Is.EqualTo(2), "New arrivals cannot extend the oldest frame's deadline.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(peer.Terminal, Is.True);
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(writer.Disconnects, Is.EqualTo(1));
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.IwantPromiseCount, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
        Assert.That(clock.ActiveTimers, Is.Zero);
    }

    [Test]
    public async Task MergedControlKeepsTheOriginalAdmissionDeadline()
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        OutboundWorkTests.PausedWriter writer = new();
        router.InboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, () => Task.CompletedTask, writer.Disconnect);
        PubsubRouter.PubsubPeer peer = Peer(router);
        Assert.That(peer.Send(new Rpc().WithTopics(["a"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.That(peer.Send(new Rpc().WithTopics(["b"], [])), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        Assert.That(peer.Entries, Is.EqualTo(1), "The second subscription merged into the pending frame.");
        Assert.That(peer.Terminal, Is.False);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(peer.Terminal, Is.True, "Merging did not restart the pending frame's deadline.");
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(writer.Disconnects, Is.EqualTo(1));
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task ExpiryCannotReleaseAnUnterminatedWriteOrAffectItsReplacement()
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.MaxOutboundRpcEntries = 2;
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        OutboundWorkTests.PausedWriter writer = new() { ReleaseOnAbort = false };
        PubsubRouter.PubsubPeer old = OutboundWorkTests.Open(router, writer);
        old.Send(OutboundWorkTests.Iwant());
        await writer.Started.Task.WaitAsync(Deadline);
        var charged = router.OutboundUsage;
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await writer.Aborted.Task.WaitAsync(Deadline);
        Assert.That(old.Terminal, Is.True);
        Assert.That(old.Completion.IsCompleted, Is.False);
        Assert.That(router.OutboundUsage, Is.EqualTo(charged));
        Assert.That(router.IwantPromiseCount, Is.Zero);
        OutboundWorkTests.PausedWriter nextWriter = new();
        PubsubRouter.PubsubPeer replacement = OutboundWorkTests.Open(router, nextWriter);
        Assert.That(replacement, Is.Not.SameAs(old));
        Assert.That(replacement.Send(OutboundWorkTests.Iwant()), Is.EqualTo(PubsubRouter.SendResult.Dropped));
        writer.Result.SetResult(IOResult.Ok);
        await old.Completion.WaitAsync(Deadline);
        Assert.That(router.IwantPromiseCount, Is.Zero, "Old success cannot recreate a cancelled promise.");
        Assert.That(replacement.Send(OutboundWorkTests.Iwant()), Is.EqualTo(PubsubRouter.SendResult.Admitted));
        await nextWriter.Started.Task.WaitAsync(Deadline);
        nextWriter.Result.SetResult(IOResult.Ok);
        await replacement.Idle.Task.WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.That(replacement.Terminal, Is.False);
        Assert.That(nextWriter.Disconnects, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task CompletionMovesTheDeadlineToTheNextFramesOriginalAdmissionTime()
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        OutboundWorkTests.PausedWriter first = new(), second = new();
        int writes = 0;
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, (frame, token) => Interlocked.Increment(ref writes) == 1
                ? first.Write(frame, token) : second.Write(frame, token), second.Abort, second.Disconnect)!;
        peer.Send(new());
        await first.Started.Task.WaitAsync(Deadline);
        clock.Advance(TimeSpan.FromMilliseconds(60));
        peer.Send(new());
        first.Result.SetResult(IOResult.Ok);
        await second.Started.Task.WaitAsync(Deadline);
        // Even a stale callback from the first frame must observe the second frame's deadline.
        clock.FireCallbacks();
        clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.That(peer.Terminal, Is.False);
        clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.That(peer.Terminal, Is.True);
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(second.Disconnects, Is.EqualTo(1));
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task DeadlineAbortsAPartlyConsumedRealChannelAndReclaimsCapacity()
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        TestChannel channel = new();
        int disconnects = 0;
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.FloodsubProtocolVersion,
            new TaskCompletionSource().Task, (frame, token) => channel.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(frame), token),
            channel.AbortAsync, () => { Interlocked.Increment(ref disconnects); return Task.CompletedTask; })!;
        peer.Send(new Dto.Rpc { Publish = { new Dto.Message { Data = Google.Protobuf.ByteString.CopyFrom(new byte[4096]) } } });
        ReadResult read = await channel.Reverse().ReadAsync(1, token: Deadline);
        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(router.OutboundUsage.Entries, Is.EqualTo(1));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(disconnects, Is.EqualTo(1));
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task LateCompletionCannotArmAPromiseWhenTheTimerCallbackIsDelayed()
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        OutboundWorkTests.PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = OutboundWorkTests.Open(router, writer);
        peer.Send(OutboundWorkTests.Iwant());
        await writer.Started.Task.WaitAsync(Deadline);
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        clock.ElapseWithoutCallbacks(TimeSpan.FromMilliseconds(100));
        writer.Result.SetResult(IOResult.Ok);
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(writer.Disconnects, Is.EqualTo(1));
        Assert.That(router.IwantPromiseCount, Is.Zero);
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    [Test]
    public async Task SystemTimerExpiresAStalledWriteWithoutHeartbeat()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        OutboundWorkTests.PausedWriter writer = new();
        PubsubRouter.PubsubPeer peer = OutboundWorkTests.Open(router, writer);
        peer.Send(new());
        await writer.Started.Task.WaitAsync(Deadline);
        await peer.Completion.WaitAsync(Deadline);
        Assert.That(writer.Disconnects, Is.EqualTo(1));
        Assert.That(peer.Terminal, Is.True);
        Assert.That(peer.Score.BehaviorPenalty, Is.Zero);
        Assert.That(router.OutboundUsage, Is.EqualTo((0, 0L)));
    }

    private static PubsubRouter.PubsubPeer Peer(PubsubRouter router) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<PeerId, PubsubRouter.PubsubPeer>)typeof(PubsubRouter)
            .GetField("peerState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!)[TestPeers.PeerId(1)];

    // One-shot timers with callbacks outside the clock lock, matching the production lock order.
    private sealed class Clock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public int ActiveTimers { get { lock (_timers) return _timers.Count; } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer timer = new(this, callback, state);
            lock (_timers) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void ElapseWithoutCallbacks(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
        public void Advance(TimeSpan elapsed)
        {
            Timer[] due;
            lock (_timers)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(t => t.Due <= _ticks).ToArray();
                foreach (Timer timer in due) timer.Due = long.MaxValue;
            }
            foreach (Timer timer in due) timer.Fire();
        }
        public void FireCallbacks()
        {
            Timer[] timers;
            lock (_timers) timers = _timers.ToArray();
            foreach (Timer timer in timers) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._timers) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
