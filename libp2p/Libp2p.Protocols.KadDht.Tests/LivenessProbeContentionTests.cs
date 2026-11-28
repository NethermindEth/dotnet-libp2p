// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// The probe reads a failed ping as evidence the peer is gone and drops the session. Two of its own
/// probes overlapping on one session is not that, and must not cost the session (#250).
/// </summary>
[TestFixture]
public class LivenessProbeContentionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public void ContentionIsRethrownWithoutDisconnectingTheSession()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ProtocolBusyException("a ping is already in flight")));

        Assert.ThrowsAsync<ProtocolBusyException>(
            async () => await LivenessProbe.ProbeAsync(session, Timeout, CancellationToken.None));

        session.DidNotReceive().DisconnectAsync();
    }

    [Test]
    public void AnyOtherFailureStillDropsTheSession()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ChannelClosedException()));

        Assert.ThrowsAsync<ChannelClosedException>(
            async () => await LivenessProbe.ProbeAsync(session, Timeout, CancellationToken.None));

        session.Received(1).DisconnectAsync();
    }

    /// <summary>
    /// Waiting for a contending ping must not be charged to the round trip that follows. A holder
    /// that runs for most of the caller's budget would otherwise leave the waiter no time to answer,
    /// and the expiry reads as a dead peer.
    /// </summary>
    /// <remarks>
    /// Driven by handoffs rather than wall-clock margins: the wait budget is cancelled while the ping
    /// is in flight, which only a ping that carries its own deadline can survive.
    /// </remarks>
    [Test]
    public async Task TheRoundTripGetsItsOwnDeadlineAfterTheWait()
    {
        TaskCompletionSource streamFreed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource refusedOnce = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource pingMayFinish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pingToken = default;
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (!streamFreed.Task.IsCompleted)
            {
                refusedOnce.TrySetResult();
                throw new ProtocolBusyException("a ping is already in flight");
            }

            pingToken = call.Arg<CancellationToken>();
            pingStarted.TrySetResult();
            await pingMayFinish.Task.WaitAsync(pingToken);
        });

        using CancellationTokenSource wait = new();
        Task probe = LivenessProbe.ProbeWaitingForStreamAsync(
            session, Timeout, CancellationToken.None, wait.Token);

        await refusedOnce.Task.WaitAsync(Timeout);
        streamFreed.TrySetResult();
        await pingStarted.Task.WaitAsync(Timeout);

        // The caller's budget runs out mid-flight. Cancelling a linked token is synchronous, so if the
        // ping were bound by that budget it would already be cancelled here.
        await wait.CancelAsync();
        Assert.That(pingToken.IsCancellationRequested, Is.False,
            "The in-flight ping must carry its own deadline, not the caller's wait budget.");

        pingMayFinish.TrySetResult();

        await probe.WaitAsync(Timeout);
        await session.DidNotReceive().DisconnectAsync();
    }

    /// <summary>
    /// A stream that never frees is still local contention. It must stay distinguishable from a probe
    /// the peer failed, so callers do not tear down a healthy session or evict a peer over it.
    /// </summary>
    [Test]
    public void AWaitThatNeverGetsTheStreamIsReportedAsContention()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ProtocolBusyException("held throughout")));
        using CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(120));

        Assert.ThrowsAsync<ProtocolBusyException>(async () => await LivenessProbe
            .ProbeWaitingForStreamAsync(session, Timeout, CancellationToken.None, wait.Token));
    }

    /// <summary>A caller giving up is cancellation, not contention, whatever the stream is doing.</summary>
    [Test]
    public void ACallerGivingUpWhileWaitingStillReportsCancellation()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ProtocolBusyException("held throughout")));
        using CancellationTokenSource caller = new(TimeSpan.FromMilliseconds(120));
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);

        // CatchAsync, not ThrowsAsync: Task.Delay surfaces the derived TaskCanceledException.
        Assert.CatchAsync<OperationCanceledException>(async () => await LivenessProbe
            .ProbeWaitingForStreamAsync(session, Timeout, caller.Token, wait.Token));
    }
}
