// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

public class NegotiationDeadlineTests
{
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

    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public int Disposals;
        public void Fire() => _callback!(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback; _state = state;
            ITimer timer = Substitute.For<ITimer>();
            timer.When(t => t.Dispose()).Do(_ => Disposals++);
            return timer;
        }
    }
}
