// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

public class IdleConnectionTests
{
    [Test]
    public async Task OnlyEstablishedSessionsPastTheIdleIntervalCanBeEvicted()
    {
        Clock clock = new();
        ProtocolRef root = new(new HoldingProtocol());
        PeerStore store = new(1);
        await using LocalPeer peer = new(new Identity(), store, new ProtocolStackSettings(), timeProvider: clock);
        using INewConnectionContext connection = peer.CreateConnection(root, null, false, null);
        connection.State.RemoteAddress = TestPeers.Multiaddr(2);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
        _ = connection.UpgradeToSession();
        await peer.Sessions.Single().Connected;
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.True);
        Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(connection.Token.IsCancellationRequested, Is.True);
        Assert.That(store.GetPeerInfo(TestPeers.Identity(2).PeerId).Sessions, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StreamNegotiationAndHandlersProtectTheConnectionUntilTheirWorkStops(bool synchronousFailure)
    {
        Clock clock = new();
        HoldingProtocol protocol = new() { FailSynchronously = synchronousFailure };
        ProtocolRef root = new(protocol);
        ProtocolStackSettings stack = new() { Protocols = new() { [root] = [new(protocol)] } };
        await using LocalPeer peer = new(new Identity(), null, stack, timeProvider: clock);
        using INewConnectionContext connection = peer.CreateConnection(root, null, false, null);
        connection.State.RemoteAddress = TestPeers.Multiaddr(2);
        INewSessionContext session = connection.UpgradeToSession();
        clock.Advance(TimeSpan.FromMinutes(2));
        Task? work = null;
        try
        {
            if (synchronousFailure)
                Assert.Throws<InvalidOperationException>(() => session.Upgrade(new Channel()));
            else
            {
                work = session.Upgrade(new Channel());
                await protocol.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
                protocol.Release.TrySetResult();
                await work;
                await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.True);
            IChannel rejected = session.Upgrade();
            Assert.That((await rejected.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        }
        finally
        {
            protocol.Release.TrySetResult();
            if (work is not null) await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            connection.Dispose();
            await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Test]
    public async Task QueuedDialsProtectTheSessionAndCancellationRestartsItsIdleInterval()
    {
        Clock clock = new();
        ProtocolRef root = new(new HoldingProtocol());
        await using LocalPeer peer = new(new Identity(), null, new ProtocolStackSettings(), timeProvider: clock);
        using INewConnectionContext connection = peer.CreateConnection(root, null, false, null);
        connection.State.RemoteAddress = TestPeers.Multiaddr(2);
        _ = connection.UpgradeToSession();
        using CancellationTokenSource request = new();
        Task dial = peer.Sessions.Single().DialAsync(new UnhandledProtocol(), request.Token);
        try
        {
            Assert.That(dial.IsCompleted, Is.False, "The request is queued without a stream worker.");
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
            request.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)));
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.False);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.That(connection.TryDisconnectIfIdle(TimeSpan.FromSeconds(60)), Is.True);
        }
        finally
        {
            request.Cancel();
            await dial.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            connection.Dispose();
            await connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class HoldingProtocol : IConnectionProtocol
    {
        public string Id => "/test/held-negotiation";
        public bool FailSynchronously;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DialAsync(IChannel channel, IConnectionContext context)
        {
            if (FailSynchronously) throw new InvalidOperationException("Injected synchronous failure");
            Entered.TrySetResult();
            return Release.Task;
        }
        public Task ListenAsync(IChannel channel, IConnectionContext context) => DialAsync(channel, context);
    }

    private sealed class UnhandledProtocol : ISessionProtocol
    {
        public string Id => "/test/queued";
        public Task DialAsync(IChannel channel, ISessionContext context) => throw new InvalidOperationException("No worker should dispatch this request.");
        public Task ListenAsync(IChannel channel, ISessionContext context) => DialAsync(channel, context);
    }

}
