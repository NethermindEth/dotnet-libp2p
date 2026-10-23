// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Reflection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Core.Exceptions;
using NSubstitute;

namespace Nethermind.Libp2p.Core.Tests;

public class ResourceDeadlineTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task InitializationDeadlineReleasesAllStorePinsAndCancelsUnderlyingWork(bool listener)
    {
        Clock clock = new();
        PeerStore store = new(3);
        await using InitializingPeer peer = new(store, clock);
        List<LocalPeer.Session> sessions = [];
        for (int i = 1; i <= 3; i++)
        {
            LocalPeer.Session session = NewSession(peer, i);
            sessions.Add(session);
            peer.UpgradeToSession(session, peer.Protocol, listener, null);
            Assert.That(store.GetPeerInfo(session.RemotePeerId).Sessions, Is.EqualTo(1));
        }
        Assert.Throws<Libp2pException>(() => peer.UpgradeToSession(NewSession(peer, 4), peer.Protocol, listener, null));
        foreach (Clock.Deadline deadline in clock.Deadlines.ToArray()) deadline.Fire();
        foreach (LocalPeer.Session session in sessions)
        {
            Assert.That(session.Closed.IsCancellationRequested, Is.True, "The deadline itself must cancel the connection.");
            await session.DisconnectAsync();
            Assert.That(session.Connected.IsCompletedSuccessfully, Is.False);
            Assert.That(store.GetPeerInfo(session.RemotePeerId).Sessions, Is.Zero);
        }
        await peer.AllStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(peer.Sessions, Is.Empty);
        peer.Hang = false;
        LocalPeer.Session healthy = NewSession(peer, 4);
        peer.UpgradeToSession(healthy, peer.Protocol, listener, null);
        await healthy.Connected.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(store.GetPeerInfo(healthy.RemotePeerId).Sessions, Is.EqualTo(1));
    }

    [Test]
    public async Task NoncooperativeInitializersStayBoundedButLoseStorePinsAtDeadline()
    {
        Clock clock = new();
        PeerStore store = new(1);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using InitializingPeer peer = new(store, clock, release.Task, new PeerResourceLimits(maxConcurrentInitializations: 1));
        LocalPeer.Session first = NewSession(peer, 1);
        peer.UpgradeToSession(first, peer.Protocol, true, null);
        clock.Deadlines.Single().Fire();
        Assert.That(first.Closed.IsCancellationRequested, Is.True);
        await first.DisconnectAsync();
        Assert.That(store.GetPeerInfo(first.RemotePeerId).Sessions, Is.Zero);
        Assert.Throws<Libp2pException>(() => peer.UpgradeToSession(NewSession(peer, 2), peer.Protocol, true, null));
        Assert.That(peer.Initializers, Is.EqualTo(1));
        release.SetResult();
        await peer.AllStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ObsoleteUpgradeAndInitializationTimersCannotCloseAnEstablishedSession()
    {
        Clock clock = new();
        await using InitializingPeer peer = new(new PeerStore(1), clock);
        LocalPeer.Session session = NewSession(peer, 1);
        using INewConnectionContext connection = peer.CreateConnection(peer.Protocol, session, true, null);
        Clock.Deadline upgrade = clock.Deadlines.Single();
        peer.Hang = false;
        connection.UpgradeToSession();
        await session.Connected;
        foreach (Clock.Deadline timer in clock.Deadlines) timer.Fire();
        Assert.That(session.Closed.IsCancellationRequested, Is.False);
        Assert.That(upgrade.Disposals, Is.EqualTo(1));
    }

    [Test]
    public async Task UpgradeDeadlineAbortsBlockedProtocolAndCompletionJoinsIt()
    {
        Clock clock = new();
        BlockingProtocol protocol = new();
        ProtocolRef transport = new(protocol);
        ProtocolRef application = new(protocol);
        ProtocolStackSettings stack = new() { Protocols = new() { [transport] = [application] } };
        await using LocalPeer peer = new(new Identity(), null, stack, timeProvider: clock);
        using INewConnectionContext connection = peer.CreateConnection(transport, null, true, null);
        _ = connection.Upgrade();
        await protocol.Entered.Task;
        clock.Deadlines.Single().Fire();
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(protocol.Stopped, Is.True);
        Assert.That(connection.Token.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task UpgradingADisconnectedConnectionReturnsAnAbortedChannelWithoutDispatch()
    {
        BlockingProtocol protocol = new();
        ProtocolRef root = new(protocol);
        ProtocolStackSettings stack = new() { Protocols = new() { [root] = [new(protocol)] } };
        await using LocalPeer peer = new(new Identity(), null, stack);
        using INewConnectionContext connection = peer.CreateConnection(root, null, false, null);
        connection.Dispose();
        IChannel channel = connection.Upgrade();
        Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(protocol.Entered.Task.IsCompleted, Is.False);
        await connection.Completion;
    }

    [Test]
    public async Task RefusalLoggingFailureDoesNotRetainCancellationRegistrations()
    {
        InboundStreamLimit limit = new(1);
        Assert.That(limit.TryAcquire(null, out _), Is.True);
        BlockingProtocol protocol = new();
        ProtocolRef root = new(protocol);
        ProtocolStackSettings stack = new() { Protocols = new() { [root] = [new(protocol, inboundLimit: limit)] } };
        using RefusalLogger logger = new();
        await using LocalPeer peer = new(new Identity(), null, stack, loggerFactory: logger);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource request = new();
        try
        {
            Assert.Throws<InvalidOperationException>(() => peer.Upgrade(session, new Channel(), root, null,
                new UpgradeOptions { CancellationToken = request.Token }, true, null));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            object? registrations = typeof(CancellationTokenSource).GetField("_registrations", flags)!.GetValue(request);
            object? callbacks = registrations?.GetType().GetField("Callbacks", flags)!.GetValue(registrations);
            Assert.That(callbacks, Is.Null, "A synchronous refusal failure must dispose the linked registration.");
            Assert.That(limit.InFlight, Is.EqualTo(1), "A refused upgrade cannot release somebody else's allowance.");
        }
        finally { limit.Release(null); await session.DisconnectAsync(); }
    }

    private sealed class RefusalLogger : ILoggerFactory, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? failure, Func<TState, Exception?, string> format)
        {
            if (format(state, failure).Contains("refused")) throw new InvalidOperationException("Injected refusal logging failure");
        }
    }

    private static LocalPeer.Session NewSession(LocalPeer peer, int remote)
    {
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(remote);
        return session;
    }

    private sealed class InitializingPeer : LocalPeer
    {
        private readonly Task? _noncooperative;
        public ProtocolRef Protocol { get; } = new(new BlockingProtocol());
        public bool Hang = true;
        public int Initializers;
        private int _stopped;
        public TaskCompletionSource AllStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InitializingPeer(PeerStore store, TimeProvider clock, Task? noncooperative = null, PeerResourceLimits? limits = null)
            : base(new Identity(), store, new ProtocolStackSettings(), resourceLimits: limits, timeProvider: clock) => _noncooperative = noncooperative;
        protected override async Task ConnectedTo(ISession session, bool isDialer)
        {
            Interlocked.Increment(ref Initializers);
            try
            {
                if (_noncooperative is not null) await _noncooperative;
                else if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, session.Closed);
            }
            finally { if (Interlocked.Increment(ref _stopped) == Initializers) AllStopped.TrySetResult(); }
        }
    }

    private sealed class BlockingProtocol : IConnectionProtocol
    {
        public string Id => "/blocked";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Stopped;
        public async Task ListenAsync(IChannel channel, IConnectionContext context)
        {
            Task<ReadResult> read = channel.ReadAsync(1, token: context.Token).AsTask();
            Entered.SetResult();
            await read;
            Stopped = true;
        }
        public Task DialAsync(IChannel channel, IConnectionContext context) => ListenAsync(channel, context);
    }

    private sealed class Clock : TimeProvider
    {
        public List<Deadline> Deadlines { get; } = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Deadline deadline = new(callback, state);
            Deadlines.Add(deadline);
            return deadline;
        }
        internal sealed class Deadline(TimerCallback callback, object? state) : ITimer
        {
            public int Disposals;
            public void Fire() => callback(state); // Include already queued callbacks from disposed timers.
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => Disposals++;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
