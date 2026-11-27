// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[CancelAfter(10_000)]
public sealed class SessionRecoveryTests
{
    private static PeerId Remote => TestPeers.PeerId(2);
    private static Multiaddress Address(int port) => $"/ip4/127.0.0.1/tcp/{port}/p2p/{Remote}";
    private static CancellationToken TestToken => TestContext.CurrentContext.CancellationToken;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    [Test]
    public async Task RecoveryRequiresOptIn()
    {
        await using Rig rig = new(enabled: false);
        Assert.Throws<InvalidOperationException>(() => rig.Peer.GetSessionRecovery());
    }

    [Test]
    public async Task SharedRecoveryRedialsUsingCurrentStoreAddressesWithoutReplayingRequests()
    {
        int delays = 0;
        await using Rig rig = new(new SessionRecoveryOptions(retryDelay: _ =>
        {
            Interlocked.Increment(ref delays);
            return RetryDelay;
        }));
        Multiaddress originalAddress = Address(4001);
        Multiaddress replacementAddress = Address(4002);
        rig.Store.Discover([originalAddress]);
        ISessionRecovery recovery = rig.Peer.GetSessionRecovery();
        Assert.That(rig.Peer.GetSessionRecovery(), Is.SameAs(recovery));

        Task<ISession> ordinaryDial = rig.Peer.DialAsync(Remote, TestToken);
        DialAttempt initial = await rig.Transport.NextAsync();
        await using IAsyncEnumerator<SessionRecoveryUpdate> first = Watch(recovery);
        await using IAsyncEnumerator<SessionRecoveryUpdate> second = Watch(recovery);
        Assert.That(await first.MoveNextAsync(), Is.True);
        Assert.That(await second.MoveNextAsync(), Is.True);
        initial.Accept.TrySetResult();

        ISession original = (await NextStateAsync(first, SessionRecoveryState.Ready)).Session!;
        Assert.That((await NextStateAsync(second, SessionRecoveryState.Ready)).Session, Is.SameAs(original));
        Assert.That(await ordinaryDial.WaitAsync(TestToken), Is.SameAs(original));
        Assert.That(initial.Address, Is.EqualTo(originalAddress));

        Task<int> lostResponse = original.DialAsync<RequestProtocol, int, int>(42, TestToken);
        INewSessionContext originalContext = await initial.Session.Task.WaitAsync(TestToken);
        Assert.That(((LocalPeer.Session)original).QueuedRequestCount, Is.EqualTo(1));
        Task invocation = originalContext.Upgrade(new Channel(), originalContext.DialRequests.First());
        Assert.That(await rig.Protocol.FirstRequest.Task.WaitAsync(TestToken), Is.EqualTo(42));
        initial.Drop.TrySetResult();
        Assert.CatchAsync<OperationCanceledException>(async () => await lostResponse.WaitAsync(TestToken));
        await invocation.WaitAsync(TestToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        SessionRecoveryUpdate retry = await NextStateAsync(first, SessionRecoveryState.Retrying);
        Assert.That(await NextStateAsync(second, SessionRecoveryState.Retrying), Is.SameAs(retry));
        Assert.That(retry.CloseReason, Is.EqualTo(SessionCloseReason.ConnectionLost));
        Assert.That(original.CloseReason, Is.EqualTo(SessionCloseReason.ConnectionLost));
        Assert.That(rig.Clock.ActiveTimersFor(RetryDelay), Is.EqualTo(1));
        rig.Store.Discover([replacementAddress]);
        rig.Clock.Advance(RetryDelay - TimeSpan.FromTicks(1));
        Assert.That(rig.Transport.DialCount, Is.EqualTo(1));
        rig.Clock.Advance(TimeSpan.FromTicks(1));

        DialAttempt replacementAttempt = await rig.Transport.NextAsync();
        Assert.That(replacementAttempt.Address, Is.EqualTo(replacementAddress));
        replacementAttempt.Accept.TrySetResult();
        ISession replacement = (await NextStateAsync(first, SessionRecoveryState.Ready)).Session!;
        Assert.That((await NextStateAsync(second, SessionRecoveryState.Ready)).Session, Is.SameAs(replacement));
        Assert.That(replacement, Is.Not.SameAs(original));
        Assert.That(replacement.IsInitialized, Is.True);
        Assert.That(((LocalPeer.Session)replacement).QueuedRequestCount, Is.Zero);
        Assert.That(rig.Protocol.Invocations, Is.EqualTo(1));
        Assert.That(rig.Transport.DialCount, Is.EqualTo(2));
        Assert.That(delays, Is.EqualTo(1));

        Task<int> newRequest = replacement.DialAsync<RequestProtocol, int, int>(43, TestToken);
        INewSessionContext replacementContext = await replacementAttempt.Session.Task.WaitAsync(TestToken);
        Assert.That(((LocalPeer.Session)replacement).QueuedRequestCount, Is.EqualTo(1));
        await replacementContext.Upgrade(new Channel(), replacementContext.DialRequests.First()).WaitAsync(TestToken);
        Assert.That(await newRequest.WaitAsync(TestToken), Is.EqualTo(43));
        Assert.That(rig.Protocol.Invocations, Is.EqualTo(2));
    }

    [Test]
    public async Task RetryExhaustionReportsTheLastFailureAndEndsTheWatch()
    {
        List<int> retries = [];
        int addressSelections = 0;
        Multiaddress firstAddress = Address(4001);
        Multiaddress secondAddress = Address(4002);
        SessionRecoveryOptions options = new(maxAttempts: 2, retryDelay: retry =>
        {
            retries.Add(retry);
            return RetryDelay;
        }, selectAddresses: (peer, _) =>
        {
            Assert.That(peer, Is.EqualTo(Remote));
            return ValueTask.FromResult(new[] { ++addressSelections == 1 ? firstAddress : secondAddress });
        });
        await using Rig rig = new(options);
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(rig.Peer.GetSessionRecovery());

        Assert.That(await watch.MoveNextAsync(), Is.True);
        DialAttempt first = await rig.Transport.NextAsync();
        Assert.That(first.Address, Is.EqualTo(firstAddress));
        IOException firstFailure = new("first address unreachable");
        first.Accept.TrySetException(firstFailure);
        SessionRecoveryUpdate retry = await NextStateAsync(watch, SessionRecoveryState.Retrying);
        Assert.That(retry.Attempt, Is.EqualTo(2));
        Assert.That(retry.Error?.InnerException, Is.SameAs(firstFailure));

        rig.Clock.Advance(RetryDelay);
        DialAttempt second = await rig.Transport.NextAsync();
        Assert.That(second.Address, Is.EqualTo(secondAddress));
        IOException lastFailure = new("last address unreachable");
        second.Accept.TrySetException(lastFailure);
        SessionRecoveryUpdate exhausted = await NextStateAsync(watch, SessionRecoveryState.Exhausted);
        Assert.That(exhausted.Attempt, Is.EqualTo(2));
        Assert.That(exhausted.Error?.InnerException, Is.SameAs(lastFailure));
        Assert.That(await watch.MoveNextAsync(), Is.False);
        Assert.That(rig.Transport.DialCount, Is.EqualTo(2));
        Assert.That(retries, Is.EqualTo(new[] { 1 }));
        Assert.That(addressSelections, Is.EqualTo(2));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task InvalidAddressPolicyFaultsWithoutRetrying(bool throws)
    {
        int selections = 0;
        InvalidOperationException failure = new("Address policy failed.");
        Multiaddress otherPeer = TestPeers.Multiaddr(3);
        SessionRecoveryOptions options = new(maxAttempts: 2, retryDelay: _ => TimeSpan.Zero, selectAddresses: (_, _) =>
        {
            selections++;
            if (throws) throw failure;
            return ValueTask.FromResult(new[] { otherPeer });
        });
        await using Rig rig = new(options);
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(rig.Peer.GetSessionRecovery());

        SessionRecoveryUpdate fault = await NextStateAsync(watch, SessionRecoveryState.Faulted);
        if (throws) Assert.That(fault.Error, Is.SameAs(failure));
        else Assert.That(fault.Error?.Message, Does.Contain(Remote.ToString()));
        Assert.That(selections, Is.EqualTo(1));
        Assert.That(rig.Transport.DialCount, Is.Zero);
        Assert.That(await watch.MoveNextAsync(), Is.False);
    }

    [Test]
    public async Task EmptyAddressSelectionCanSucceedOnTheNextAttempt()
    {
        int selections = 0;
        Multiaddress address = Address(4001);
        SessionRecoveryOptions options = new(maxAttempts: 2, retryDelay: _ => RetryDelay, selectAddresses: (_, _) =>
            ValueTask.FromResult<Multiaddress[]>(++selections == 1 ? [] : [address]));
        await using Rig rig = new(options);
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(rig.Peer.GetSessionRecovery());

        SessionRecoveryUpdate retry = await NextStateAsync(watch, SessionRecoveryState.Retrying);
        Assert.That(retry.Attempt, Is.EqualTo(2));
        Assert.That(rig.Transport.DialCount, Is.Zero);
        rig.Clock.Advance(RetryDelay);
        DialAttempt attempt = await rig.Transport.NextAsync();
        Assert.That(attempt.Address, Is.EqualTo(address));
        attempt.Accept.SetResult();
        Assert.That((await NextStateAsync(watch, SessionRecoveryState.Ready)).Attempt, Is.EqualTo(2));
    }

    [TestCase(SessionCloseReason.LocalDisconnect)]
    [TestCase(SessionCloseReason.PeerShutdown)]
    public async Task DeliberateStopsPublishTheirReasonBeforeClosedFires(SessionCloseReason reason)
    {
        await using Rig rig = new();
        rig.Store.Discover([Address(4001)]);
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(rig.Peer.GetSessionRecovery());
        (ISession session, DialAttempt attempt) = await ConnectAsync(rig, watch);
        TaskCompletionSource<SessionCloseReason> notified = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = session.Closed.Register(() => notified.TrySetResult(session.CloseReason));

        if (reason == SessionCloseReason.PeerShutdown)
            await rig.Peer.DisposeAsync();
        else
            await session.DisconnectAsync();

        Assert.That(await notified.Task.WaitAsync(TestToken), Is.EqualTo(reason));
        await attempt.Ended.Task.WaitAsync(TestToken);
        Assert.That(session.CloseReason, Is.EqualTo(reason));
        Assert.That((await NextStateAsync(watch, SessionRecoveryState.Stopped)).CloseReason, Is.EqualTo(reason));
        Assert.That(await watch.MoveNextAsync(), Is.False);
        Assert.That(rig.Transport.DialCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ConnectedHandlerFailureClosesTheSessionWithoutRetrying()
    {
        await using Rig rig = new(new SessionRecoveryOptions(retryDelay: _ => RetryDelay));
        rig.Store.Discover([Address(4001)]);
        TaskCompletionSource failHandler = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Peer.OnConnected += _ =>
        {
            failHandler.Task.WaitAsync(TestToken).GetAwaiter().GetResult();
            throw new InvalidOperationException("Application connection handler failed.");
        };
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(rig.Peer.GetSessionRecovery());
        ISession session;
        DialAttempt attempt;
        try
        {
            (session, attempt) = await ConnectAsync(rig, watch);
            Assert.That(session.Closed.IsCancellationRequested, Is.False);
        }
        finally { failHandler.TrySetResult(); }

        await attempt.Ended.Task.WaitAsync(TestToken);
        Assert.That(session.CloseReason, Is.EqualTo(SessionCloseReason.LocalDisconnect));
        Assert.That(await watch.MoveNextAsync().AsTask().WaitAsync(TestToken), Is.True);
        Assert.That(watch.Current.State, Is.EqualTo(SessionRecoveryState.Stopped));
        Assert.That(watch.Current.CloseReason, Is.EqualTo(SessionCloseReason.LocalDisconnect));
        Assert.That(await watch.MoveNextAsync().AsTask().WaitAsync(TestToken), Is.False);
        Assert.That(rig.Clock.ActiveTimersFor(RetryDelay), Is.Zero);
        Assert.That(rig.Transport.DialCount, Is.EqualTo(1));
    }

    [Test]
    public async Task PeerShutdownCancelsAnActiveBackoff()
    {
        await using Rig rig = new(new SessionRecoveryOptions(retryDelay: _ => RetryDelay));
        rig.Store.Discover([Address(4001)]);
        ISessionRecovery recovery = rig.Peer.GetSessionRecovery();
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(recovery);

        (_, DialAttempt attempt) = await ConnectAsync(rig, watch);
        attempt.Drop.TrySetResult();
        await NextStateAsync(watch, SessionRecoveryState.Retrying);
        Assert.That(rig.Clock.ActiveTimersFor(RetryDelay), Is.EqualTo(1));

        await rig.Peer.DisposeAsync();
        Assert.That((await NextStateAsync(watch, SessionRecoveryState.Stopped)).CloseReason, Is.EqualTo(SessionCloseReason.PeerShutdown));
        Assert.That(rig.Clock.ActiveTimersFor(RetryDelay), Is.Zero);
        rig.Clock.Advance(RetryDelay);
        Assert.That(rig.Transport.DialCount, Is.EqualTo(1));

        await using IAsyncEnumerator<SessionRecoveryUpdate> afterShutdown = Watch(recovery);
        Assert.That((await NextStateAsync(afterShutdown, SessionRecoveryState.Stopped)).CloseReason, Is.EqualTo(SessionCloseReason.PeerShutdown));
    }

    [Test]
    public async Task CancelingOneWatchAndDisposingTheLastLeaveTheSharedSessionAlive()
    {
        CancellationToken recoveryToken = default;
        SessionRecoveryOptions options = new(selectAddresses: (_, token) =>
        {
            recoveryToken = token;
            return ValueTask.FromResult(new[] { Address(4001) });
        });
        await using Rig rig = new(options);
        ISessionRecovery recovery = rig.Peer.GetSessionRecovery();
        using CancellationTokenSource firstToken = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        await using IAsyncEnumerator<SessionRecoveryUpdate> first = Watch(recovery, firstToken.Token);
        IAsyncEnumerator<SessionRecoveryUpdate> second = Watch(recovery);

        ISession session;
        DialAttempt attempt;
        try
        {
            (session, attempt) = await ConnectAsync(rig, first);
            Assert.That((await NextStateAsync(second, SessionRecoveryState.Ready)).Session, Is.SameAs(session));
            firstToken.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await first.MoveNextAsync());
            Assert.That(recoveryToken.IsCancellationRequested, Is.False);
            Assert.That(await rig.Peer.DialAsync(Remote, TestToken), Is.SameAs(session));
        }
        finally { await second.DisposeAsync(); }

        Assert.That(recoveryToken.IsCancellationRequested, Is.True);
        Assert.That(session.Closed.IsCancellationRequested, Is.False);
        attempt.Drop.TrySetResult();
        await attempt.Ended.Task.WaitAsync(TestToken);
        Assert.That(rig.Transport.DialCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task NewWatchWaitsForCanceledLookupOrDialToRetire(bool holdDial)
    {
        TaskCompletionSource<CancellationToken> oldEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource oldClosed = new();
        using CancellationTokenSource newClosed = new();
        ISession oldSession = new LegacyClosingSession(oldClosed.Token);
        ISession newSession = new LegacyClosingSession(newClosed.Token);
        Multiaddress oldAddress = Address(4001);
        Multiaddress newAddress = Address(4002);
        int lookups = 0;
        List<Multiaddress> dialed = [];
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>())
            .Returns(call => DialAsync(call.Arg<Multiaddress[]>().Single(), call.Arg<CancellationToken>()));
        SessionRecoveryOptions options = new(selectAddresses: async (_, token) =>
        {
            if (Interlocked.Increment(ref lookups) != 1) return [newAddress];
            if (!holdDial) await HoldOldAsync(token);
            return [oldAddress];
        });

        await using SessionRecovery recovery = new(peer, options, TimeProvider.System);
        using CancellationTokenSource firstToken = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        await using IAsyncEnumerator<SessionRecoveryUpdate> first = Watch(recovery, firstToken.Token);
        await using IAsyncEnumerator<SessionRecoveryUpdate> second = Watch(recovery);
        try
        {
            Assert.That(await first.MoveNextAsync(), Is.True);
            CancellationToken oldToken = await oldEntered.Task.WaitAsync(TestToken);
            firstToken.Cancel();
            Assert.That(oldToken.IsCancellationRequested, Is.True);

            Task<bool> joining = second.MoveNextAsync().AsTask();
            Assert.That(joining.IsCompleted, Is.False);
            Assert.That(Volatile.Read(ref lookups), Is.EqualTo(1));
            releaseOld.TrySetResult();

            Assert.That(await joining.WaitAsync(TestToken), Is.True);
            SessionRecoveryUpdate ready = second.Current.State == SessionRecoveryState.Ready
                ? second.Current : await NextStateAsync(second, SessionRecoveryState.Ready);
            Assert.That(ready.Session, Is.SameAs(newSession));
            Assert.That(dialed, Is.EqualTo(holdDial ? new[] { oldAddress, newAddress } : [newAddress]));
            Assert.That(oldClosed.IsCancellationRequested, Is.False);
        }
        finally { releaseOld.TrySetResult(); }

        async Task HoldOldAsync(CancellationToken token)
        {
            oldEntered.TrySetResult(token);
            await releaseOld.Task;
        }

        async Task<ISession> DialAsync(Multiaddress address, CancellationToken token)
        {
            dialed.Add(address);
            if (address == newAddress) return newSession;
            if (holdDial) await HoldOldAsync(token);
            return oldSession;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CustomSessionsNeedCancelableClosureAndExplicitLossClassification(bool hasCloseToken)
    {
        using CancellationTokenSource closed = new();
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        ISession session = hasCloseToken ? new LegacyClosingSession(closed.Token) : new TestRemotePeer(TestPeers.Multiaddr(2));
        peer.DialAsync(Remote, Arg.Any<CancellationToken>()).Returns(session);
        await using SessionRecovery recovery = new(peer, new SessionRecoveryOptions(retryDelay: _ => TimeSpan.Zero), TimeProvider.System);
        await using IAsyncEnumerator<SessionRecoveryUpdate> watch = Watch(recovery);

        if (hasCloseToken)
        {
            Assert.That((await NextStateAsync(watch, SessionRecoveryState.Ready)).Session, Is.SameAs(session));
            closed.Cancel();
            Assert.That((await NextStateAsync(watch, SessionRecoveryState.Stopped)).CloseReason, Is.EqualTo(SessionCloseReason.Unknown));
        }
        else
        {
            SessionRecoveryUpdate unsupported = await NextStateAsync(watch, SessionRecoveryState.Faulted);
            Assert.That(unsupported.Error, Is.TypeOf<NotSupportedException>());
        }
        Assert.That(await watch.MoveNextAsync(), Is.False);
        await peer.Received(1).DialAsync(Remote, Arg.Any<CancellationToken>());
    }

    private static IAsyncEnumerator<SessionRecoveryUpdate> Watch(ISessionRecovery recovery, CancellationToken token = default)
        => recovery.WatchAsync(Remote).GetAsyncEnumerator(token.CanBeCanceled ? token : TestToken);

    private static async Task<SessionRecoveryUpdate> NextStateAsync(IAsyncEnumerator<SessionRecoveryUpdate> watch, SessionRecoveryState state)
    {
        while (await watch.MoveNextAsync().AsTask().WaitAsync(TestToken))
        {
            if (watch.Current.State == state) return watch.Current;
            if (watch.Current.IsTerminal)
                throw new AssertionException($"Expected {state}, received {watch.Current.State}: {watch.Current.Error}");
        }
        throw new AssertionException($"The recovery watch ended before {state}.");
    }

    private static async Task<(ISession Session, DialAttempt Attempt)> ConnectAsync(Rig rig, IAsyncEnumerator<SessionRecoveryUpdate> watch)
    {
        Assert.That(await watch.MoveNextAsync(), Is.True);
        DialAttempt attempt = await rig.Transport.NextAsync();
        attempt.Accept.TrySetResult();
        return ((await NextStateAsync(watch, SessionRecoveryState.Ready)).Session!, attempt);
    }

    private sealed class LegacyClosingSession(CancellationToken closed) : TestRemotePeer(TestPeers.Multiaddr(2)), ISession
    {
        public CancellationToken Closed => closed;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public ManualTimeProvider Clock { get; } = new();
        public PeerStore Store { get; } = new();
        public ControlledTransport Transport { get; } = new();
        public RequestProtocol Protocol { get; } = new();
        public ILocalPeer Peer { get; }
        private readonly RecoveryBuilder _builder;

        public Rig(SessionRecoveryOptions? options = null, bool enabled = true)
        {
            _builder = new(Transport, Clock, Store);
            _builder.AddProtocol(Protocol);
            if (enabled) _builder.WithSessionRecovery(options);
            Peer = _builder.Build().Create(TestPeers.Identity(1));
        }

        public async ValueTask DisposeAsync()
        {
            try { await Peer.DisposeAsync(); }
            finally { await _builder.DisposeAsync(); }
        }
    }

    private sealed class RecoveryBuilder : PeerFactoryBuilderBase<RecoveryBuilder, PeerFactory>
    {
        private readonly ControlledTransport _transport;

        public RecoveryBuilder(ControlledTransport transport, ManualTimeProvider clock, IPeerStore store)
        {
            _transport = transport;
            InternalServices.AddSingleton<TimeProvider>(clock).AddSingleton(store);
        }

        protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
        {
            ProtocolRef transport = new(_transport);
            Connect([transport], additionalProtocols.ToArray());
            return [transport];
        }
    }

    private sealed class DialAttempt(Multiaddress address)
    {
        public Multiaddress Address { get; } = address;
        public TaskCompletionSource Accept { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Drop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<INewSessionContext> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledTransport : ITransportProtocol
    {
        private readonly Channel<DialAttempt> _attempts = System.Threading.Channels.Channel.CreateUnbounded<DialAttempt>();
        private int _dialCount;
        public int DialCount => Volatile.Read(ref _dialCount);
        public string Id => "/test/recovery";
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [$"/p2p/{peerId}"];
        public static bool IsAddressMatch(Multiaddress addr) => true;

        public ValueTask<DialAttempt> NextAsync() => _attempts.Reader.ReadAsync(TestToken);

        public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
        {
            DialAttempt attempt = new(remoteAddr);
            Interlocked.Increment(ref _dialCount);
            _attempts.Writer.TryWrite(attempt);
            try
            {
                await attempt.Accept.Task.WaitAsync(token);
                using INewConnectionContext connection = context.CreateConnection();
                connection.State.RemoteAddress = remoteAddr;
                using INewSessionContext session = connection.UpgradeToSession();
                attempt.Session.TrySetResult(session);
                try { await attempt.Drop.Task.WaitAsync(connection.Token); }
                catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
            }
            finally { attempt.Ended.TrySetResult(); }
        }

        public Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class RequestProtocol : ISessionProtocol<int, int>
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public TaskCompletionSource<int> FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "/test/recovery-request";

        public async Task<int> DialAsync(IChannel channel, ISessionContext context, int request)
        {
            if (Interlocked.Increment(ref _invocations) == 1)
            {
                FirstRequest.TrySetResult(request);
                await Task.Delay(Timeout.InfiniteTimeSpan, context.UpgradeOptions!.CancellationToken);
            }
            return request;
        }

        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
    }
}
