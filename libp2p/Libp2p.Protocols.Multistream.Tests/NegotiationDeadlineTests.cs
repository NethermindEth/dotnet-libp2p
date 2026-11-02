// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

public class NegotiationDeadlineTests
{
    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task NegotiationExpiresAtTheConfiguredDeadline(bool listen, bool configured)
    {
        ManualClock clock = new();
        MultistreamProtocol protocol = new(timeProvider: clock);
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        TimeSpan expected = TimeSpan.FromSeconds(configured ? 7 : 15);
        if (configured) context.ResourceLimits.Returns(new PeerResourceLimits(negotiationTimeout: expected));
        Task negotiation = listen ? protocol.ListenAsync(channel, context) : protocol.DialAsync(channel, context);
        try
        {
            await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await remote.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(new byte[] { 128 }));
            clock.Advance(expected - TimeSpan.FromMilliseconds(1));
            Assert.That(negotiation.IsCompleted, Is.False);
            Assert.That(channel.GetAwaiter().IsCompleted, Is.False);
            clock.Advance(TimeSpan.FromMilliseconds(1));
            await negotiation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
        }
        finally { await remote.AbortAsync(); await negotiation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeadlineAbortsPartialNegotiationAndJoinsItsWork(bool listen)
    {
        ManualClock clock = new();
        MultistreamProtocol protocol = new(timeProvider: clock);
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        Task negotiation = listen ? protocol.ListenAsync(channel, context) : protocol.DialAsync(channel, context);
        try
        {
            Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(protocol.Id));
            // A fragmented varint reaches an actual blocked negotiation read.
            await remote.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(new byte[] { 128 }));
            clock.Fire();
            await negotiation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
            await context.DidNotReceiveWithAnyArgs().Upgrade(default!, default(IProtocol)!);
            Assert.That(clock.Disposals, Is.EqualTo(1));
        }
        finally { await remote.AbortAsync(); }
    }

    [Test]
    public async Task NegotiationTimerStopsBeforeLongLivedSelectedHandlerRuns()
    {
        ManualClock clock = new();
        MultistreamProtocol protocol = new(timeProvider: clock);
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = Substitute.For<IProtocol>();
        selected.Id.Returns("/long-lived");
        context.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = selected });
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Upgrade(channel, selected).Returns(_ => { entered.TrySetResult(); return release.Task; });
        Task negotiation = protocol.DialAsync(channel, context);
        try
        {
            await remote.ReadLineAsync();
            await remote.ReadLineAsync();
            await remote.WriteLineAsync(protocol.Id);
            await remote.WriteLineAsync(selected.Id);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            clock.Fire();
            Assert.That(clock.Disposals, Is.EqualTo(1));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.False);
            Assert.That(negotiation.IsCompleted, Is.False);
        }
        finally
        {
            release.TrySetResult();
            await remote.AbortAsync();
            await negotiation.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [TestCase(0)] // Listener hello
    [TestCase(1)] // Listener proposal
    [TestCase(2)] // Dialer hello
    [TestCase(3)] // Dialer proposal reply
    [TestCase(4)] // Optimistic dialer hello
    [TestCase(5)] // Optimistic dialer proposal reply
    public async Task OversizedNegotiationIsRejectedBeforeReadingItsPayload(int stage)
    {
        MultistreamProtocol protocol = new();
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = Substitute.For<IProtocol>();
        selected.Id.Returns("/test/bounded");
        context.SubProtocols.Returns([selected]);
        if (stage >= 4) context.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = selected });
        Task negotiation = stage < 2 ? protocol.ListenAsync(channel, context) : protocol.DialAsync(channel, context);
        try
        {
            Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(protocol.Id));
            if (stage >= 4) await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (stage % 2 == 1)
            {
                await remote.WriteLineAsync(protocol.Id);
                if (stage == 3) await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            await remote.WriteVarintAsync(1025);
            await negotiation.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
            await context.DidNotReceiveWithAnyArgs().Upgrade(default!, default(IProtocol)!);
        }
        finally
        {
            await remote.AbortAsync();
            await negotiation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        private TimeSpan _remaining;
        public int Disposals;
        public void Fire() => _callback!(_state);
        public void Advance(TimeSpan elapsed)
        {
            if (_remaining == Timeout.InfiniteTimeSpan || Disposals != 0) return;
            _remaining -= elapsed;
            if (_remaining <= TimeSpan.Zero) Fire();
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback; _state = state; _remaining = dueTime;
            ITimer timer = Substitute.For<ITimer>();
            timer.When(t => t.Dispose()).Do(_ => Disposals++);
            return timer;
        }
    }
}
