// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Core.Tests;

internal class SessionInvocationCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task QueueSkipsCanceledRequestsBeforeOpeningAStream()
    {
        Probe canceled = new();
        Probe active = new();
        ProtocolRef parent = new(active);
        await using LocalPeer peer = CreatePeer(parent, active);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource cancellation = new();
        Task canceledDial = session.DialAsync(canceled, cancellation.Token);
        Task activeDial = session.DialAsync(active);
        try
        {
            Assert.That(session.QueuedRequestCount, Is.EqualTo(2));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await canceledDial.WaitAsync(Timeout));

            UpgradeOptions request = session.GetRequestQueue().First();
            Assert.That(request.SelectedProtocol, Is.SameAs(active));
            active.Release.TrySetResult();
            await peer.Upgrade(session, new Channel(), parent, request.SelectedProtocol, request, false, null);
            await activeDial.WaitAsync(Timeout);
            Assert.That(canceled.Calls, Is.Zero);
            Assert.That(active.Calls, Is.EqualTo(1));
            Assert.That(session.QueuedRequestCount, Is.Zero);
        }
        finally
        {
            await session.DisconnectAsync();
            await Task.WhenAll(canceledDial, activeDial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task DisconnectDrainsPendingRequests()
    {
        Probe protocol = new();
        await using LocalPeer peer = CreatePeer(new(protocol), protocol);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        Task[] pending = Enumerable.Range(0, 16).Select(_ => session.DialAsync(protocol, caller.Token)).ToArray();
        Assert.That(session.QueuedRequestCount, Is.EqualTo(pending.Length));

        await session.DisconnectAsync();

        foreach (Task dial in pending)
        {
            OperationCanceledException? failure = Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));
            Assert.That(failure!.CancellationToken, Is.EqualTo(session.Closed));
        }
        Assert.That(session.QueuedRequestCount, Is.Zero);
        Assert.That(protocol.Calls, Is.Zero);
        caller.Cancel();
    }

    [Test]
    public async Task ConcurrentDialsCannotEnqueueAfterDisconnect()
    {
        Probe protocol = new();
        await using LocalPeer peer = CreatePeer(new(protocol), protocol);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            LocalPeer.Session session = new(peer);
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] pending = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                try { await session.DialAsync(protocol); }
                catch (OperationCanceledException) { }
            })).ToArray();
            Task disconnect = Task.Run(async () =>
            {
                await start.Task;
                await session.DisconnectAsync();
            });
            start.SetResult();
            await Task.WhenAll(pending.Append(disconnect)).WaitAsync(Timeout);
            Assert.That(session.QueuedRequestCount, Is.Zero);
        }
        Assert.That(protocol.Calls, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationAfterDequeueStillPreventsInvocation(bool listener)
    {
        Probe protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        Task dial = session.DialAsync(protocol, caller.Token);
        UpgradeOptions options = session.GetRequestQueue().First();
        caller.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await peer.Upgrade(session, new Channel(), parent, protocol, options, listener, null));
        Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));

        Assert.That(protocol.Calls, Is.Zero);
        await session.DisconnectAsync();
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    [TestCase(false, true, false)]
    public async Task RunningInvocationObservesItsLifetime(bool listener, bool tokenSupplied, bool cancelCaller)
    {
        Probe protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        UpgradeOptions? options = tokenSupplied ? new() { SelectedProtocol = protocol, CancellationToken = caller.Token } : null;
        Task work = peer.Upgrade(session, new Channel(), parent, protocol, options, listener, null);
        try
        {
            Assert.That(protocol.Calls, Is.EqualTo(1));
            Assert.That(protocol.Token.CanBeCanceled, Is.True);
            if (cancelCaller) caller.Cancel();
            else await session.DisconnectAsync();

            await protocol.Canceled.Task.WaitAsync(Timeout);
            Assert.CatchAsync<OperationCanceledException>(async () => await work.WaitAsync(Timeout));
            Assert.That(protocol.Token.IsCancellationRequested, Is.True);
            Assert.That(session.Closed.IsCancellationRequested, Is.EqualTo(!cancelCaller));
            Assert.That(caller.IsCancellationRequested, Is.EqualTo(cancelCaller));
        }
        finally
        {
            protocol.Release.TrySetResult();
            await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await session.DisconnectAsync();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task CancellationWinsWhenProtocolReturnsFromTokenCallback(bool cancelCaller, bool closeBeforeCancel)
    {
        ResultProtocol protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        Task<int> dial = session.DialAsync<ResultProtocol, int>(caller.Token);
        UpgradeOptions request = session.GetRequestQueue().First();
        Channel channel = new();
        Task upgrade = peer.Upgrade(session, channel.Reverse, parent, protocol, request, false, null);
        using CancellationTokenRegistration release = protocol.Token.Register(() => protocol.Response.TrySetResult(42));
        try
        {
            Assert.That(upgrade.IsCompleted, Is.False);
            if (closeBeforeCancel) await channel.CloseAsync();
            if (cancelCaller) caller.Cancel();
            else await session.DisconnectAsync();

            Assert.That(protocol.Response.Task.IsCompletedSuccessfully, Is.True);
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));
            Assert.That(dial.IsCanceled, Is.True);
            if (closeBeforeCancel) await upgrade.WaitAsync(Timeout);
            else Assert.CatchAsync<OperationCanceledException>(async () => await upgrade.WaitAsync(Timeout));
            Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(closeBeforeCancel ? IOResult.Ended : IOResult.Aborted));
            Assert.That(session.Closed.IsCancellationRequested, Is.EqualTo(!cancelCaller));
        }
        finally
        {
            protocol.Response.TrySetResult(42);
            await session.DisconnectAsync();
            await Task.WhenAll(upgrade, dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task CancellationCancelsDialWithoutAbortingChannelThatFinishedClosing()
    {
        ResultProtocol protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol);
        LocalPeer.Session session = new(peer);
        Task<int> dial = session.DialAsync<ResultProtocol, int>();
        UpgradeOptions request = session.GetRequestQueue().First();
        IChannel channel = Substitute.For<IChannel>();
        TaskCompletionSource close = new();
        channel.CloseAsync().Returns(new ValueTask(close.Task));
        channel.GetAwaiter().Returns(close.Task.GetAwaiter());
        Task upgrade = peer.Upgrade(session, channel, parent, protocol, request, false, null);
        using CancellationTokenRegistration release = protocol.Token.Register(() => close.TrySetResult());
        try
        {
            Assert.That(protocol.Response.TrySetResult(42), Is.True);
            _ = channel.Received(1).CloseAsync();
            Assert.That(upgrade.IsCompleted, Is.False);

            await session.DisconnectAsync();

            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));
            Assert.That(dial.IsCanceled, Is.True);
            await upgrade.WaitAsync(Timeout);
            await channel.DidNotReceive().AbortAsync();
        }
        finally
        {
            close.TrySetResult();
            await session.DisconnectAsync();
            await Task.WhenAll(upgrade, dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellationAfterUpgradeTokenIsUnlinkedCancelsTheDial(bool cancelCaller)
    {
        ResultProtocol protocol = new();
        ProtocolRef parent = new(protocol);
        using ActivitySource activitySource = new(nameof(CancellationAfterUpgradeTokenIsUnlinkedCancelsTheDial));
        await using LocalPeer peer = CreatePeer(parent, protocol, activitySource);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        CancellationToken source = cancelCaller ? caller.Token : session.Closed;
        Task<int> dial = session.DialAsync<ResultProtocol, int>(caller.Token);
        UpgradeOptions request = session.GetRequestQueue().First();
        TaskCompletionSource cancellationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseCallbacks = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? cancel = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = candidate => candidate == activitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (!Equals(activity.GetTagItem("proto"), protocol.Id)) return;
                // Cleanup disposes the linked source before stopping the upgrade activity.
                cancel = cancelCaller ? caller.CancelAsync() : Task.Run(session.DisconnectAsync);
                cancellationStarted.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
            }
        };
        ActivitySource.AddActivityListener(listener);

        Channel channel = new();
        Task upgrade = peer.Upgrade(session, channel.Reverse, parent, protocol, request, false, null);
        // The last registered callback runs first. Hold the dial's source registration
        // behind it until the upgrade has tried to publish the result.
        using CancellationTokenRegistration pause = source.Register(() =>
        {
            cancellationStarted.TrySetResult();
            releaseCallbacks.Task.WaitAsync(Timeout).GetAwaiter().GetResult();
        });
        try
        {
            protocol.Response.SetResult(42);
            await upgrade.WaitAsync(Timeout);

            Assert.That(source.IsCancellationRequested, Is.True);
            Assert.That(protocol.Token.IsCancellationRequested, Is.False);
            Assert.That(cancel?.IsCompleted, Is.False);
            Assert.That(request.CompletionSource!.Task.IsCanceled, Is.True);
            releaseCallbacks.TrySetResult();
            OperationCanceledException? failure = Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));
            Assert.That(failure!.CancellationToken, Is.EqualTo(source));
            Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
        }
        finally
        {
            releaseCallbacks.TrySetResult();
            if (cancel is not null) await cancel.WaitAsync(Timeout);
            await session.DisconnectAsync();
            await Task.WhenAll(upgrade, dial).WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task ChannelReadFailureCancelsDialOnlyWhenLifetimeIsCanceled(bool cancelCaller, bool closeOnly)
    {
        TaskCompletionSource releaseCallbacks = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ReadingProtocol protocol = new(releaseCallbacks.Task);
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        Task<int> dial = session.DialAsync<ReadingProtocol, int>(caller.Token);
        UpgradeOptions request = session.GetRequestQueue().First();
        Channel channel = new();
        Task upgrade = peer.Upgrade(session, channel.Reverse, parent, protocol, request, false, null);
        using CancellationTokenRegistration pause = protocol.Pause;
        Task? cancel = null;
        try
        {
            Assert.That(protocol.Read.IsCompleted, Is.False);
            if (closeOnly)
            {
                await channel.CloseAsync();
                ChannelClosedException? failure = Assert.ThrowsAsync<ChannelClosedException>(async () => await dial.WaitAsync(Timeout));
                Assert.That(failure, Is.SameAs(protocol.Read.Exception!.InnerException));
                Assert.That(protocol.Token.IsCancellationRequested, Is.False);
            }
            else
            {
                cancel = cancelCaller ? caller.CancelAsync() : Task.Run(session.DisconnectAsync);
                // Observe publication before releasing the core and session callbacks.
                OperationCanceledException? failure = Assert.CatchAsync<OperationCanceledException>(
                    async () => await request.CompletionSource!.Task.WaitAsync(Timeout));
                Assert.That(request.CompletionSource!.Task.IsCanceled, Is.True);
                Assert.That(failure!.CancellationToken, Is.EqualTo(protocol.Token));
                releaseCallbacks.TrySetResult();
                Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Timeout));
                Assert.That(dial.IsCanceled, Is.True);
            }

            ChannelClosedException? diagnostic = Assert.ThrowsAsync<ChannelClosedException>(async () => await upgrade.WaitAsync(Timeout));
            Assert.That(diagnostic, Is.SameAs(protocol.Read.Exception!.InnerException));
        }
        finally
        {
            releaseCallbacks.TrySetResult();
            if (cancel is not null) await cancel.WaitAsync(Timeout);
            await session.DisconnectAsync();
            await Task.WhenAll(upgrade, dial).WaitAsync(Timeout).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static LocalPeer CreatePeer(ProtocolRef parent, IProtocol protocol, ActivitySource? activitySource = null) => new(
        TestPeers.Identity(1), null, new ProtocolStackSettings { Protocols = new() { [parent] = [new(protocol)] } }, activitySource);

    private sealed class Probe : SymmetricSessionProtocol, ISessionProtocol
    {
        public string Id => "/test/invocation-cancellation/1.0.0";
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task ConnectAsync(IChannel channel, ISessionContext context, bool isListener)
        {
            Calls++;
            Token = context.UpgradeOptions?.CancellationToken ?? default;
            try { await Release.Task.WaitAsync(Token); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class ResultProtocol : ISessionProtocol<int>
    {
        public string Id => "/test/callback-completion/1.0.0";
        public CancellationToken Token { get; private set; }
        // Allow the upgrade continuation to run inside the callback that sets this result.
        public TaskCompletionSource<int> Response { get; } = new();

        public Task<int> DialAsync(IChannel channel, ISessionContext context)
        {
            Token = context.UpgradeOptions!.CancellationToken;
            return Response.Task;
        }

        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
    }

    private sealed class ReadingProtocol(Task releaseCallbacks) : ISessionProtocol<int>
    {
        public string Id => "/test/read-cancellation/1.0.0";
        public CancellationToken Token { get; private set; }
        public CancellationTokenRegistration Pause { get; private set; }
        public Task<int> Read { get; private set; } = null!;

        public Task<int> DialAsync(IChannel channel, ISessionContext context)
        {
            Token = context.UpgradeOptions!.CancellationToken;
            // Register before the real read so it can fail while the earlier
            // upgrade cancellation callback is still waiting to run.
            Pause = Token.Register(() => releaseCallbacks.WaitAsync(Timeout).GetAwaiter().GetResult());
            return Read = channel.ReadVarintAsync(Token);
        }

        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
    }
}
