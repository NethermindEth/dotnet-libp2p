// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Metrics;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[CancelAfter(5_000)]
internal class PeerLifecycleTests
{
    [Test]
    public async Task CanceledInitializationDoesNotConnectSession()
    {
        ControlledPeer peer = new(TestPeers.Identity(1), connectedTo: () => Task.FromCanceled(new CancellationToken(true)));
        LocalPeer.Session session = NewSession(peer);
        bool connected = false;
        peer.OnConnected += _ => connected = true;

        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);

        Assert.CatchAsync<OperationCanceledException>(async () => await session.Connected.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(SpinWait.SpinUntil(() => peer.Sessions.Count == 0, TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(connected, Is.False);
    }

    [Test]
    public async Task CanceledInitializationFailsTheDial()
    {
        GateTransport transport = new();
        ControlledPeer peer = new(TestPeers.Identity(1), transport, () => Task.FromCanceled(new CancellationToken(true)));
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        bool connected = false;
        peer.OnConnected += _ => connected = true;

        Task<ISession> dial = peer.DialAsync(remoteAddress);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompleteDial.SetResult();

        Exception failure = Assert.CatchAsync<Exception>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)))!;
        Assert.That(failure, Is.Not.InstanceOf<TimeoutException>());
        Assert.That(connected, Is.False);
        Assert.That(peer.Sessions, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExistingSessionDialWaitsForInitializationAndHonorsCallerCancellation(bool usePeerId)
    {
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledPeer peer = new(TestPeers.Identity(1), connectedTo: () => initialization.Task);
        LocalPeer.Session session = NewSession(peer);
        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), true, null);

        using CancellationTokenSource canceled = new();
        Task<ISession> canceledWaiter = peer.DialAsync(session.RemoteAddress, canceled.Token);
        Task<ISession> continuingWaiter = usePeerId
            ? peer.DialAsync(session.RemoteAddress.GetPeerId()!)
            : peer.DialAsync(session.RemoteAddress);
        Assert.That(continuingWaiter.IsCompleted, Is.False);

        canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await canceledWaiter.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(continuingWaiter.IsCompleted, Is.False);

        initialization.SetResult();
        Assert.That(await continuingWaiter.WaitAsync(TimeSpan.FromSeconds(2)), Is.SameAs(session));
        await session.DisconnectAsync();
    }

    [Test]
    public async Task FailedInitializationDoesNotReturnExistingSession()
    {
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledPeer peer = new(TestPeers.Identity(1), connectedTo: () => initialization.Task);
        LocalPeer.Session session = NewSession(peer);
        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), true, null);

        Task<ISession> dial = peer.DialAsync(session.RemoteAddress);
        initialization.SetException(new InvalidOperationException("initialization failed"));

        Assert.ThrowsAsync<InvalidOperationException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(SpinWait.SpinUntil(() => peer.Sessions.Count == 0, TimeSpan.FromSeconds(2)), Is.True);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task DeduplicatedDialCancellationOnlyEndsTheCanceledWait(bool cancelFirst)
    {
        GateTransport transport = new();
        ControlledPeer peer = new(TestPeers.Identity(1), transport);
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        using CancellationTokenSource firstCancellation = new();
        using CancellationTokenSource secondCancellation = new();

        Task<ISession> first = peer.DialAsync(remoteAddress, firstCancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ISession> second = peer.DialAsync(remoteAddress, secondCancellation.Token);

        (CancellationTokenSource canceled, Task<ISession> canceledWaiter, Task<ISession> continuingWaiter) =
            cancelFirst ? (firstCancellation, first, second) : (secondCancellation, second, first);
        canceled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await canceledWaiter.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(continuingWaiter.IsCompleted, Is.False);

        transport.CompleteDial.SetResult();
        ISession session = await continuingWaiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(transport.DialCount, Is.EqualTo(1));
        await session.DisconnectAsync();
    }

    [Test]
    public async Task CancelingAllDialWaitersAllowsASeparateRetry()
    {
        GateTransport transport = new();
        ControlledPeer peer = new(TestPeers.Identity(1), transport);
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        using CancellationTokenSource firstCancellation = new();
        using CancellationTokenSource secondCancellation = new();

        Task<ISession> first = peer.DialAsync(remoteAddress, firstCancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ISession> second = peer.DialAsync(remoteAddress, secondCancellation.Token);
        firstCancellation.Cancel();
        secondCancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.CatchAsync<OperationCanceledException>(async () => await second.WaitAsync(TimeSpan.FromSeconds(2)));
        await transport.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<ISession> retry = peer.DialAsync(remoteAddress);
        transport.CompleteDial.SetResult();
        ISession session = await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(transport.DialCount, Is.EqualTo(2));
        await session.DisconnectAsync();
    }

    [Test]
    public async Task CancelingLastWaiterAfterUpgradeDisconnectsPendingSession()
    {
        GateTransport transport = new();
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledPeer peer = new(TestPeers.Identity(1), transport, () => initialization.Task);
        using CancellationTokenSource cancellation = new();

        Task<ISession> dial = peer.DialAsync(TestPeers.Multiaddr(2), cancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompleteDial.SetResult();
        Assert.That(SpinWait.SpinUntil(() => peer.Sessions.Count == 1, TimeSpan.FromSeconds(2)), Is.True);

        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)));
        try
        {
            await transport.ConnectionClosed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(peer.Sessions, Is.Empty);
        }
        finally
        {
            foreach (LocalPeer.Session session in peer.Sessions.ToArray())
            {
                await session.DisconnectAsync();
            }
            initialization.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallerJoiningAfterUpgradeKeepsSharedDialAlive(bool usePeerId)
    {
        GateTransport transport = new();
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledPeer peer = new(TestPeers.Identity(1), transport, () => initialization.Task);
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        using CancellationTokenSource cancellation = new();

        Task<ISession> first = peer.DialAsync(remoteAddress, cancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompleteDial.SetResult();
        Assert.That(SpinWait.SpinUntil(() => peer.Sessions.Count == 1, TimeSpan.FromSeconds(2)), Is.True);
        Task<ISession> second = usePeerId
            ? peer.DialAsync(remoteAddress.GetPeerId()!)
            : peer.DialAsync(remoteAddress);
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.That(second.IsCompleted, Is.False);

        initialization.SetResult();
        ISession session = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(transport.DialCount, Is.EqualTo(1));
        Assert.That(transport.PostUpgradeDialCanceled.Task.IsCompleted, Is.False);
        await session.DisconnectAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    [NonParallelizable]
    public async Task InitializationFailureCountsFailedDial(bool canceled)
    {
        Activity? dialActivity = null;
        using ActivitySource activitySource = new(nameof(InitializationFailureCountsFailedDial));
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => source == activitySource,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                if (activity.OperationName.StartsWith("Dial ")) dialActivity = activity;
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        long attempts = 0;
        long failures = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument == Libp2pMetrics.DialAttempts || instrument == Libp2pMetrics.DialFailures)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument == Libp2pMetrics.DialAttempts) Interlocked.Add(ref attempts, value);
            if (instrument == Libp2pMetrics.DialFailures) Interlocked.Add(ref failures, value);
        });
        listener.Start();

        GateTransport transport = new(holdAfterConnectionClosed: true);
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledPeer peer = new(TestPeers.Identity(1), transport, () => initialization.Task, activitySource);
        Task<ISession> dial = peer.DialAsync(TestPeers.Multiaddr(2));
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompleteDial.SetResult();
        Assert.That(SpinWait.SpinUntil(() => peer.Sessions.Count == 1, TimeSpan.FromSeconds(2)), Is.True);

        try
        {
            if (canceled)
            {
                initialization.SetCanceled();
            }
            else
            {
                initialization.SetException(new InvalidOperationException("initialization failed"));
            }
            Exception failure = Assert.CatchAsync<Exception>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)))!;
            Assert.That(failure, Is.Not.InstanceOf<TimeoutException>());
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(dialActivity?.Status, Is.EqualTo(ActivityStatusCode.Error));
        }
        finally
        {
            transport.ReleaseAfterConnectionClosed.TrySetResult();
        }
    }

    [Test]
    [NonParallelizable]
    public async Task RepeatedDisconnectAndRejectedDuplicateBalanceActiveMetrics()
    {
        long connections = 0;
        long sessions = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter == Libp2pMetrics.Meter && instrument is UpDownCounter<long>)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument == Libp2pMetrics.ConnectionsActive) Interlocked.Add(ref connections, value);
            if (instrument == Libp2pMetrics.SessionsActive) Interlocked.Add(ref sessions, value);
        });
        listener.Start();

        ControlledPeer peer = new(TestPeers.Identity(1));
        ProtocolRef protocol = new(new GateTransport());
        LocalPeer.Session admitted = NewSession(peer);
        peer.CreateConnection(protocol, admitted, false, null);
        peer.UpgradeToSession(admitted, protocol, false, null);
        await admitted.Connected.WaitAsync(TimeSpan.FromSeconds(2));

        LocalPeer.Session duplicate = NewSession(peer);
        peer.CreateConnection(protocol, duplicate, false, null);
        Assert.Throws<Exceptions.SessionExistsException>(() => peer.UpgradeToSession(duplicate, protocol, false, null));
        await duplicate.DisconnectAsync();
        await admitted.DisconnectAsync();
        await admitted.DisconnectAsync();

        Assert.That(peer.Sessions, Is.Empty);
        Assert.That(connections, Is.Zero);
        Assert.That(sessions, Is.Zero);
    }

    private static LocalPeer.Session NewSession(LocalPeer peer)
    {
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);
        return session;
    }

    private sealed class ControlledPeer(Identity identity, GateTransport? transport = null, Func<Task>? connectedTo = null, ActivitySource? activitySource = null)
        : LocalPeer(identity, null, new ProtocolStackSettings(), activitySource)
    {
        protected override ProtocolRef SelectProtocol(Multiaddress addr) => new(transport ?? new GateTransport());
        protected override Task ConnectedTo(ISession session, bool isDialer) => connectedTo?.Invoke() ?? Task.CompletedTask;
    }

    private sealed class GateTransport(bool holdAfterConnectionClosed = false) : ITransportProtocol
    {
        public string Id => "gate";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectionClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PostUpgradeDialCanceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAfterConnectionClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteDial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DialCount;

        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [$"/p2p/{peerId}"];
        public static bool IsAddressMatch(Multiaddress addr) => true;

        public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
        {
            Interlocked.Increment(ref DialCount);
            Started.TrySetResult();
            try
            {
                await CompleteDial.Task.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
            INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = remoteAddr;
            using INewSessionContext session = connection.UpgradeToSession();
            using CancellationTokenRegistration registration = token.Register(() => PostUpgradeDialCanceled.TrySetResult());
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, connection.Token);
            }
            catch (OperationCanceledException) when (connection.Token.IsCancellationRequested)
            {
                ConnectionClosed.TrySetResult();
                if (holdAfterConnectionClosed) await ReleaseAfterConnectionClosed.Task;
            }
        }

        public Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token) => Task.CompletedTask;
    }
}
