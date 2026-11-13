// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.TestsBase;

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

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task RunningInvocationObservesItsLifetime(bool listener, bool cancelCaller)
    {
        Probe protocol = new();
        ProtocolRef parent = new(protocol);
        await using LocalPeer peer = CreatePeer(parent, protocol);
        LocalPeer.Session session = new(peer);
        using CancellationTokenSource caller = new();
        UpgradeOptions? options = cancelCaller ? new() { SelectedProtocol = protocol, CancellationToken = caller.Token } : null;
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
        }
        finally
        {
            protocol.Release.TrySetResult();
            await work.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await session.DisconnectAsync();
        }
    }

    private static LocalPeer CreatePeer(ProtocolRef parent, IProtocol protocol) => new(TestPeers.Identity(1), null,
        new ProtocolStackSettings { Protocols = new() { [parent] = [new(protocol)] } });

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
}
