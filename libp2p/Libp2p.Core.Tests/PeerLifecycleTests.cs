// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Metrics;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using Nethermind.Libp2p.Core.Discovery;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[CancelAfter(5_000)]
internal class PeerLifecycleTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RemoteDiscoveryRefreshesAddressesAfterConnecting(bool disconnect)
    {
        PeerStore store = new(1);
        store.Discover([$"/ip4/192.0.2.10/tcp/4001/p2p/{TestPeers.PeerId(2)}"], isTrusted: false);
        ControlledPeer peer = new(TestPeers.Identity(1), store: store);
        LocalPeer.Session session = NewSession(peer);
        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);
        await session.Connected;
        try
        {
            if (disconnect) await session.DisconnectAsync();
            Multiaddress updated = $"/ip4/192.0.2.20/tcp/4001/p2p/{TestPeers.PeerId(2)}";

            store.Discover([updated], isTrusted: false);

            Assert.That(store.ReadPeerInfo(TestPeers.PeerId(2)).Addresses, Is.EquivalentTo(new[] { updated }));
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [Test]
    public async Task SessionChurnCannotDisplaceConfiguredSeeds()
    {
        PeerStore store = new(2);
        Multiaddress seed = TestPeers.Multiaddr(2);
        store.Discover([seed]);
        ControlledPeer peer = new(TestPeers.Identity(1), store: store);

        for (int i = 3; i < 12; i++)
        {
            LocalPeer.Session session = new(peer);
            session.State.RemoteAddress = TestPeers.Multiaddr(i);
            peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);
            try
            {
                await session.Connected;
                store.SetSupportedProtocols(TestPeers.PeerId(i), ["/meshsub/1.3.0"]);
            }
            finally
            {
                await session.DisconnectAsync();
            }
            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out var retained), Is.True);
            Assert.That(retained!.Addresses, Is.EquivalentTo(new[] { seed }));
        }
    }

    [Test]
    public async Task FullConfiguredStoreRejectsRemoteAdmissionWithoutEvictingASeed()
    {
        PeerStore store = new(1);
        store.Discover([TestPeers.Multiaddr(3)]);
        ControlledPeer peer = new(TestPeers.Identity(1), store: store);
        LocalPeer.Session session = NewSession(peer);
        using INewConnectionContext connection = peer.CreateConnection(new ProtocolRef(new GateTransport()), session, true, null);

        try
        {
            Assert.Throws<Libp2pException>(() => connection.UpgradeToSession());

            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(3), out _), Is.True);
            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [Test]
    public async Task FullRetainedStoreRejectsAnotherSessionAndCleansUpItsConnection()
    {
        PeerStore store = new(1);
        ControlledPeer peer = new(TestPeers.Identity(1), store: store);
        LocalPeer.Session first = NewSession(peer);
        peer.UpgradeToSession(first, new ProtocolRef(new GateTransport()), false, null);
        await first.Connected;
        LocalPeer.Session second = new(peer);
        second.State.RemoteAddress = TestPeers.Multiaddr(3);
        using INewConnectionContext connection = peer.CreateConnection(new ProtocolRef(new GateTransport()), second, true, null);
        try
        {
            Libp2pException? error = Assert.Throws<Libp2pException>(() => connection.UpgradeToSession());
            Assert.That(error!.Message, Does.Contain("capacity 1"));
            Assert.That(second.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.EqualTo(new[] { first }));
            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.True);
            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(3), out _), Is.False);
        }
        finally
        {
            await first.DisconnectAsync();
            await second.DisconnectAsync();
        }
    }

    [Test]
    public async Task SharedStoreRetainsPeerUntilEverySessionDisconnects()
    {
        PeerStore store = new(1);
        ControlledPeer firstPeer = new(TestPeers.Identity(1), store: store);
        ControlledPeer secondPeer = new(TestPeers.Identity(3), store: store);
        LocalPeer.Session first = NewSession(firstPeer);
        LocalPeer.Session second = NewSession(secondPeer);
        firstPeer.UpgradeToSession(first, new ProtocolRef(new GateTransport()), false, null);
        secondPeer.UpgradeToSession(second, new ProtocolRef(new GateTransport()), false, null);
        try
        {
            await first.DisconnectAsync();
            Assert.Throws<Libp2pException>(() => store.SetAgentVersion(TestPeers.PeerId(4), null));
            await second.DisconnectAsync();
            store.SetAgentVersion(TestPeers.PeerId(4), null);
            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(4), out _), Is.True);
        }
        finally
        {
            await first.DisconnectAsync();
            await second.DisconnectAsync();
        }
    }

    [Test]
    public async Task ActiveSessionKeepsMetadataAttachedDuringConcurrentCacheChurn()
    {
        PeerStore store = new(2);
        ControlledPeer peer = new(TestPeers.Identity(1), store: store);
        LocalPeer.Session session = NewSession(peer);
        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);
        await session.Connected;
        PeerId remote = TestPeers.PeerId(2);
        store.SetAgentVersion(remote, "client/1");
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task update = Task.Run(async () =>
        {
            ready.SetResult();
            await release.Task;
            store.SetSupportedProtocols(remote, ["/meshsub/1.3.0"]);
        });
        try
        {
            await ready.Task;
            Parallel.For(3, 30, i => store.SetAgentVersion(TestPeers.PeerId(i), null));
            release.SetResult();
            await update;

            Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out PeerMetadata? current), Is.True);
            Assert.That(current!.AgentVersion, Is.EqualTo("client/1"));
            Assert.That(current.SupportedProtocols, Is.EqualTo(new[] { "/meshsub/1.3.0" }));
            Assert.That(await peer.DialAsync(TestPeers.PeerId(2)), Is.SameAs(session));
        }
        finally
        {
            release.TrySetResult();
            await update;
            await session.DisconnectAsync();
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task SessionReleasesRetentionWhenInitializationEnds(int completion)
    {
        PeerStore store = new(1);
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ControlledPeer peer = new(TestPeers.Identity(1), connectedTo: () => initialization.Task, store: store);
        LocalPeer.Session session = NewSession(peer);
        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.True);
        Assert.Throws<Libp2pException>(() => store.SetAgentVersion(TestPeers.PeerId(3), null), "An active session must not be evicted to admit another peer.");

        if (completion != 0)
        {
            TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.Sessions.CollectionChanged += (_, _) => { if (peer.Sessions.Count == 0) removed.TrySetResult(); };
            if (completion == 1)
            {
                initialization.SetException(new InvalidOperationException("initialization failed"));
                Assert.CatchAsync<InvalidOperationException>(async () => await session.Connected);
            }
            else
            {
                initialization.SetCanceled();
                Assert.CatchAsync<OperationCanceledException>(async () => await session.Connected);
            }
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else
        {
            initialization.SetResult();
            await session.Connected;
            await session.DisconnectAsync();
            await session.DisconnectAsync();
        }

        store.SetAgentVersion(TestPeers.PeerId(3), null);
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(3), out _), Is.True);
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
    }

    [TestCase(17)]
    [TestCase(33)]
    public async Task LiteralFallbackBeyondDnsLimitsIsDialed(int count)
    {
        Multiaddress[] roots = Enumerable.Range(1, count)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        GateTransport transport = new() { OnlyReachableAddress = roots[^1] };
        transport.CompleteDial.SetResult();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);

        ISession session = await peer.DialAsync(roots, CancellationToken.None);

        Assert.That(session.RemoteAddress.ToString(), Is.EqualTo(roots[^1].ToString()));
        Assert.That(transport.DialCount, Is.EqualTo(count));
        await session.DisconnectAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MalformedRootDoesNotPreventDialingTheValidAddress(bool bareFirst)
    {
        GateTransport transport = new();
        transport.CompleteDial.SetResult();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);
        Multiaddress bare = "/ip4/127.0.0.1/tcp/4001";
        Multiaddress known = TestPeers.Multiaddr(2);

        ISession session = await peer.DialAsync(bareFirst ? [bare, known] : [known, bare], CancellationToken.None);

        Assert.That(session.RemoteAddress, Is.EqualTo(known));
        Assert.That(transport.DialCount, Is.EqualTo(1));
        await session.DisconnectAsync();
    }

    [Test]
    public async Task ForeignRootDoesNotPreventDialingTheFirstIdentifiedPeer()
    {
        GateTransport transport = new();
        transport.CompleteDial.SetResult();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);

        ISession session = await peer.DialAsync([TestPeers.Multiaddr(2), TestPeers.Multiaddr(3)], CancellationToken.None);

        Assert.That(session.RemoteAddress, Is.EqualTo(TestPeers.Multiaddr(2)));
        Assert.That(transport.DialCount, Is.EqualTo(1));
        await session.DisconnectAsync();
    }

    [Test]
    public async Task AddressesWithoutAnyPeerIdAreReportedExplicitly()
    {
        GateTransport transport = new();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);
        Multiaddress bare = "/ip4/127.0.0.1/tcp/4001";

        Libp2pException? error = Assert.ThrowsAsync<Libp2pException>(async () => await peer.DialAsync(bare));

        Assert.That(error!.Message, Does.Contain("missing a peer id"));
        Assert.That(transport.DialCount, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LiteralCandidatesHaveBoundedConcurrentDials(bool fromStore)
    {
        Multiaddress[] roots = Enumerable.Range(1, 65)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        PeerStore store = new();
        store.Discover(roots, isTrusted: false);
        GateTransport transport = new();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport, store: store);
        using CancellationTokenSource cancellation = new();
        Task<ISession> dial = fromStore ? peer.DialAsync(TestPeers.PeerId(2), cancellation.Token) : peer.DialAsync(roots, cancellation.Token);
        try
        {
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(transport.DialCount, Is.EqualTo(16), "Caller literals remain candidates, but only sixteen attempts may run at once.");
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)));
            await transport.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(transport.DialCount, Is.EqualTo(16), "Cancellation must not start queued candidates.");
        }
        finally
        {
            cancellation.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task FailedCandidateFreesASlotWithoutWaitingForOtherAttempts()
    {
        Multiaddress[] roots = Enumerable.Range(1, 33)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        TaskCompletionSource failFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GateTransport transport = new()
        {
            BeforeDial = async (address, token) =>
            {
                if (address == roots[0])
                {
                    await failFirst.Task.WaitAsync(token);
                    throw new IOException("First address failed");
                }
                if (address != roots[16]) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        transport.CompleteDial.SetResult();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);
        using CancellationTokenSource cancellation = new();
        Task<ISession> dial = peer.DialAsync(roots, cancellation.Token);
        try
        {
            Assert.That(transport.DialCount, Is.EqualTo(16));
            failFirst.SetResult();

            ISession session = await dial.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(session.RemoteAddress, Is.EqualTo(roots[16]));
            Assert.That(transport.DialCount, Is.EqualTo(17), "A free slot must reach the next candidate while the other fifteen remain pending.");
            await session.DisconnectAsync();
        }
        finally
        {
            cancellation.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StoredBadAddressesCannotChangeTheDialedPeer(bool validFirst)
    {
        IPeerStore store = Substitute.For<IPeerStore>();
        Multiaddress valid = TestPeers.Multiaddr(2);
        Multiaddress foreign = TestPeers.Multiaddr(3);
        Multiaddress bare = "/ip4/192.0.2.1/tcp/4001";
        PeerMetadata metadata = new(addresses: validFirst ? [valid, bare, foreign] : [foreign, bare, valid]);
        store.TryGetPeerInfo(TestPeers.PeerId(2), out Arg.Any<PeerMetadata?>()).Returns(call =>
        {
            call[1] = metadata;
            return true;
        });
        GateTransport transport = new();
        transport.CompleteDial.SetResult();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport, store: store);

        ISession session = await peer.DialAsync(TestPeers.PeerId(2));

        Assert.That(session.RemoteAddress, Is.EqualTo(valid));
        Assert.That(transport.DialCount, Is.EqualTo(1));
        await session.DisconnectAsync();
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task FailedCandidatesPreserveEachConnectionError(int count)
    {
        Multiaddress[] roots = Enumerable.Range(1, count)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        GateTransport transport = new() { OnlyReachableAddress = TestPeers.Multiaddr(2) };
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);

        Exception failure = Assert.CatchAsync<Exception>(async () => await peer.DialAsync(roots, CancellationToken.None))!;

        Exception[] failures = failure is AggregateException aggregate ? aggregate.InnerExceptions.ToArray() : [failure];
        Assert.That(failures, Has.Length.EqualTo(count));
        Assert.That(failures, Is.All.TypeOf<PeerConnectionException>());
        Assert.That(failures.Cast<PeerConnectionException>().Select(error => error.RemoteAddress), Is.EqualTo(roots.Select(address => address.ToString())));
        Assert.That(peer.Sessions, Is.Empty);
    }

    [Test]
    public async Task FailedCandidatesDoNotAccumulateSessionsBesideAHangingAttempt()
    {
        Multiaddress[] roots = Enumerable.Range(1, 65)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        GateTransport transport = new() { OnlyReachableAddress = roots[0] };
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);
        using CancellationTokenSource cancellation = new();
        Task<ISession> dial = peer.DialAsync(roots, cancellation.Token);
        try
        {
            Assert.That(transport.DialCount, Is.EqualTo(65));
            Lock pendingLock = (Lock)typeof(LocalPeer).GetField("_pendingDialsLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(peer)!;
            lock (pendingLock)
            {
                var pendingDials = (System.Collections.IDictionary)typeof(LocalPeer).GetField("_pendingDials", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(peer)!;
                object pending = pendingDials[TestPeers.PeerId(2)]!;
                var sessions = (ICollection<LocalPeer.Session>)pending.GetType().GetProperty("Sessions")!.GetValue(pending)!;
                Assert.That(sessions, Has.Count.EqualTo(1), "Only the still-pending attempt may retain session state.");
            }
        }
        finally
        {
            cancellation.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task SynchronousTransportFailureReleasesItsSessionAndRetention()
    {
        PeerStore store = new(1);
        ThrowingTransport transport = new();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport, store: store);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await peer.DialAsync(TestPeers.Multiaddr(2)));

        Assert.That(peer.Sessions, Is.Empty);
        Assert.That(transport.ConnectionToken.IsCancellationRequested, Is.True);
        store.SetAgentVersion(TestPeers.PeerId(3), null);
        Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(3), out _), Is.True, "A failed transport must release the peer-store slot.");
    }

    [Test]
    public async Task DuplicateSessionFailureEndsTheDialAndCancelsOtherCandidates()
    {
        Multiaddress[] roots = Enumerable.Range(1, 33)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        SessionExistsException duplicate = new(TestPeers.PeerId(2));
        GateTransport transport = new()
        {
            BeforeDial = (address, token) => address == roots[1] ? Task.FromException(duplicate) : Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport);

        SessionExistsException? failure = Assert.ThrowsAsync<SessionExistsException>(async () => await peer.DialAsync(roots, CancellationToken.None));

        Assert.That(failure, Is.SameAs(duplicate));
        Assert.That(transport.DialCount, Is.EqualTo(16));
        await transport.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(peer.Sessions, Is.Empty);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task FailedInitializationDoesNotDivertJoiningCallersFromFallback(bool usePeerId, bool lookupBeforeFailure)
    {
        Multiaddress[] roots = [$"/ip4/192.0.2.1/tcp/4001/p2p/{TestPeers.PeerId(2)}", $"/ip4/192.0.2.2/tcp/4001/p2p/{TestPeers.PeerId(2)}"];
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GateTransport transport = new()
        {
            BeforeDial = (address, token) => address == roots[0] ? Task.CompletedTask : releaseFallback.Task.WaitAsync(token)
        };
        transport.CompleteDial.SetResult();
        BlockingFailureLogger logger = new();
        using ILoggerFactory factory = logger;
        int initializations = 0;
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport,
            () => Interlocked.Increment(ref initializations) == 1 ? initialization.Task : Task.CompletedTask, loggerFactory: factory);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Sessions.CollectionChanged += (_, _) => { if (peer.Sessions.Count == 0) removed.TrySetResult(); };
        Task<ISession> first = peer.DialAsync(roots, CancellationToken.None);
        LocalPeer.Session captured = peer.Sessions.Single();
        Task<ISession>? joining = null;
        try
        {
            initialization.SetException(new InvalidOperationException("first initialization failed"));
            await logger.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await transport.PostUpgradeDialCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));

            joining = lookupBeforeFailure
                ? (Task<ISession>)typeof(LocalPeer).GetMethod("WaitForExistingSessionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(peer, [TestPeers.PeerId(2), captured, CancellationToken.None])!
                : usePeerId ? peer.DialAsync(TestPeers.PeerId(2)) : peer.DialAsync(roots[0]);

            Assert.That(joining.IsCompleted, Is.False, "A joining caller must keep waiting for the shared dial's remaining fallback.");
            logger.Release.Set();
            await removed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseFallback.SetResult();
            ISession session = await first.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(await joining.WaitAsync(TimeSpan.FromSeconds(2)), Is.SameAs(session));
            Assert.That(session.RemoteAddress, Is.EqualTo(roots[1]));
            await session.DisconnectAsync();
        }
        finally
        {
            logger.Release.Set();
            releaseFallback.TrySetResult();
            await ((Task)first).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (joining is not null) await ((Task)joining).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task EmptyResolutionIsReportedAsResolutionFailure()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<string>>([]));
        await using ControlledPeer peer = new(TestPeers.Identity(1), resolver: new MultiaddrResolver(dns));

        Libp2pException? error = Assert.ThrowsAsync<Libp2pException>(async () =>
            await peer.DialAsync((Multiaddress)$"/dnsaddr/missing.test/p2p/{TestPeers.PeerId(2)}"));

        Assert.That(error!.Message, Does.Contain("No usable address was resolved"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SessionsExposeRegisteredInboundAndOutboundSessionsThroughTheirLifecycle(bool inbound)
    {
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ControlledPeer local = new(TestPeers.Identity(1), connectedTo: () => initialization.Task);
        ILocalPeer peer = local;
        LocalPeer.Session session = NewSession(local);
        using INewConnectionContext connection = local.CreateConnection(new(new GateTransport()), session, inbound, null);
        Assert.That(peer.Sessions, Is.Empty, "Transport connections are hidden before session admission.");

        TaskCompletionSource<ISession> connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OnConnected += value => connected.TrySetResult(value);
        using INewSessionContext context = connection.UpgradeToSession();
        try
        {
            ISession observed = peer.Sessions.Single();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(observed, Is.SameAs(session));
                Assert.That(observed.RemotePeerId, Is.EqualTo(TestPeers.PeerId(2)));
                Assert.That(observed.RemoteAddress, Is.EqualTo(TestPeers.Multiaddr(2)));
                Assert.That(observed.IsInitialized, Is.False);
                Assert.That(connected.Task.IsCompleted, Is.False);
            }

            initialization.SetResult();
            Assert.That(await connected.Task.WaitAsync(TestContext.CurrentContext.CancellationToken), Is.SameAs(observed));
            Assert.That(observed.IsInitialized, Is.True);

            IReadOnlyList<ISession>? closing = null;
            using CancellationTokenRegistration registration = observed.Closed.Register(() => closing = peer.Sessions);
            await observed.DisconnectAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(closing, Is.EqualTo(new[] { observed }));
                Assert.That(observed.Closed.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
            }
        }
        finally
        {
            initialization.TrySetResult();
        }
    }

    [Test]
    public async Task SessionsSnapshotCanBeEnumeratedDuringAdmissionAndRemoval()
    {
        await using ControlledPeer local = new(TestPeers.Identity(1));
        ILocalPeer peer = local;
        LocalPeer.Session first = NewSession(local);
        LocalPeer.Session second = new(local) { State = { RemoteAddress = TestPeers.Multiaddr(3) } };
        LocalPeer.Session third = new(local) { State = { RemoteAddress = TestPeers.Multiaddr(4) } };
        ProtocolRef protocol = new(new GateTransport());
        using INewSessionContext firstContext = local.UpgradeToSession(first, protocol, false, null);
        using INewSessionContext secondContext = local.UpgradeToSession(second, protocol, true, null);

        IReadOnlyList<ISession> snapshot = peer.Sessions;
        using IEnumerator<ISession> enumeration = snapshot.GetEnumerator();
        Assert.That(enumeration.MoveNext(), Is.True);
        ISession firstObserved = enumeration.Current;

        using INewSessionContext thirdContext = await Task.Run(async () =>
        {
            await first.DisconnectAsync();
            return local.UpgradeToSession(third, protocol, false, null);
        });

        Assert.That(enumeration.MoveNext(), Is.True);
        ISession secondObserved = enumeration.Current;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { firstObserved, secondObserved }, Is.EquivalentTo(new[] { first, second }));
            Assert.That(enumeration.MoveNext(), Is.False);
            Assert.That(snapshot, Is.EquivalentTo(new[] { first, second }));
            Assert.That(peer.Sessions, Is.EquivalentTo(new[] { second, third }));
            if (snapshot is System.Collections.IList list)
            {
                Assert.Throws<NotSupportedException>(() => list[0] = third);
            }
        }
    }

    [Test]
    public async Task SessionLifetimeEndsApplicationWorkOnDisconnect()
    {
        ControlledPeer peer = new(TestPeers.Identity(1));
        LocalPeer.Session session = NewSession(peer);
        ISession exposed = session;
        int notifications = 0;
        using CancellationTokenRegistration registration = exposed.Closed.Register(() => notifications++);
        Task work = Task.Delay(Timeout.Infinite, exposed.Closed);
        Assert.That(exposed.RemotePeerId, Is.EqualTo(TestPeers.PeerId(2)));
        Assert.That(exposed.Closed.CanBeCanceled, Is.True);
        Assert.That(exposed.Closed.IsCancellationRequested, Is.False);

        await session.DisconnectAsync();
        await session.DisconnectAsync();

        Assert.That(exposed.Closed.IsCancellationRequested, Is.True);
        Assert.That(work.IsCanceled, Is.True);
        Assert.That(notifications, Is.EqualTo(1));
        Assert.That(exposed.RemotePeerId, Is.EqualTo(TestPeers.PeerId(2)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedInitializationEndsExposedSessionLifetime(bool canceled)
    {
        ControlledPeer peer = new(TestPeers.Identity(1), connectedTo: () => canceled
            ? Task.FromCanceled(new CancellationToken(true))
            : Task.FromException(new InvalidOperationException("initialization failed")));
        LocalPeer.Session session = NewSession(peer);
        ISession exposed = session;

        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);

        Assert.CatchAsync<Exception>(async () => await session.Connected);
        Assert.That(exposed.Closed.IsCancellationRequested, Is.True);
        Assert.That(peer.Sessions, Is.Empty);
    }

    [Test]
    public async Task PeerDisposalEndsExposedSessionLifetime()
    {
        ControlledPeer peer = new(TestPeers.Identity(1));
        LocalPeer.Session session = NewSession(peer);
        peer.UpgradeToSession(session, new ProtocolRef(new GateTransport()), false, null);
        await session.Connected;

        await peer.DisposeAsync();

        Assert.That(((ISession)session).Closed.IsCancellationRequested, Is.True);
    }

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

    [TestCase(true, false)]
    [TestCase(false, false)]
    [TestCase(true, true)]
    [TestCase(false, true)]
    public async Task DeduplicatedDialCancellationOnlyEndsTheCanceledWait(bool cancelFirst, bool secondByPeerId)
    {
        GateTransport transport = new();
        ControlledPeer peer = new(TestPeers.Identity(1), transport);
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        using CancellationTokenSource firstCancellation = new();
        using CancellationTokenSource secondCancellation = new();

        Task<ISession> first = peer.DialAsync(remoteAddress, firstCancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ISession> second = secondByPeerId ? peer.DialAsync(TestPeers.PeerId(2), secondCancellation.Token) : peer.DialAsync(remoteAddress, secondCancellation.Token);

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

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancelingAllDialWaitersAllowsASeparateRetry(bool secondByPeerId)
    {
        GateTransport transport = new();
        ControlledPeer peer = new(TestPeers.Identity(1), transport);
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);
        using CancellationTokenSource firstCancellation = new();
        using CancellationTokenSource secondCancellation = new();

        Task<ISession> first = peer.DialAsync(remoteAddress, firstCancellation.Token);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<ISession> second = secondByPeerId ? peer.DialAsync(TestPeers.PeerId(2), secondCancellation.Token) : peer.DialAsync(remoteAddress, secondCancellation.Token);
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
    public async Task SynchronousDialFailureDoesNotCacheFailedAttempt()
    {
        GateTransport transport = new();
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<string>>([]));
        ControlledPeer peer = new(TestPeers.Identity(1), transport, resolver: new MultiaddrResolver(dns));
        Multiaddress remoteAddress = TestPeers.Multiaddr(2);

        Assert.ThrowsAsync<Libp2pException>(async () =>
            await peer.DialAsync((Multiaddress)$"/dnsaddr/missing.test/p2p/{remoteAddress.GetPeerId()}"));

        Task<ISession> retry = peer.DialAsync(remoteAddress);
        Assert.That(retry.IsCompleted, Is.False, "the failed attempt must not be reused");
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.CompleteDial.SetResult();
        ISession session = await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(transport.DialCount, Is.EqualTo(1));
        await session.DisconnectAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedNamesAndForeignRecordsDoNotPreventDialingTheValidAddress(bool includeFailedName)
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        dns.QueryTxtAsync("_dnsaddr.broken.test", Arg.Any<CancellationToken>()).Returns(Task.FromException<IEnumerable<string>>(new DnsClient.DnsResponseException("lookup failed")));
        GateTransport transport = new();
        transport.CompleteDial.SetResult();
        ControlledPeer peer = new(TestPeers.Identity(1), transport, resolver: new MultiaddrResolver(dns));
        Multiaddress valid = TestPeers.Multiaddr(2);
        dns.QueryTxtAsync("_dnsaddr.root.test", Arg.Any<CancellationToken>()).Returns(Task.FromResult<IEnumerable<string>>(
            [$"dnsaddr={TestPeers.Multiaddr(3)}", $"dnsaddr={valid}"]));
        Multiaddress root = $"/dnsaddr/root.test/p2p/{valid.GetPeerId()}";

        Multiaddress[] addresses = includeFailedName
            ? [$"/dnsaddr/broken.test/p2p/{valid.GetPeerId()}", root]
            : [root];
        ISession session = await peer.DialAsync(addresses, CancellationToken.None);
        try
        {
            Assert.That(session.RemoteAddress.ToString(), Is.EqualTo(valid.ToString()));
            Assert.That(transport.DialCount, Is.EqualTo(1));
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [Test]
    public async Task CancelingDialCancelsThePendingDnsQuery()
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IEnumerable<string>> PendingQuery(CancellationToken token)
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Array.Empty<string>();
            }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
        }
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => PendingQuery(call.Arg<CancellationToken>()));
        ControlledPeer peer = new(TestPeers.Identity(1), resolver: new MultiaddrResolver(dns));
        using CancellationTokenSource cancellation = new();
        Task<ISession> dial = peer.DialAsync((Multiaddress)$"/dnsaddr/pending.test/p2p/{TestPeers.PeerId(2)}", cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TimeSpan.FromSeconds(2)));
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StalledDnsDoesNotDiscardLiteralSibling(bool stalledFirst)
    {
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IEnumerable<string>> PendingQuery(CancellationToken token)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return [];
        }
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => PendingQuery(call.Arg<CancellationToken>()));
        ResolutionClock clock = new();
        GateTransport transport = new();
        transport.CompleteDial.SetResult();
        ControlledPeer peer = new(TestPeers.Identity(1), transport, resolver: new MultiaddrResolver(dns, clock));
        Multiaddress valid = TestPeers.Multiaddr(2);
        Multiaddress stalled = $"/dnsaddr/stalled.test/p2p/{valid.GetPeerId()}";
        Task<ISession> dial = peer.DialAsync(stalledFirst ? [stalled, valid] : [valid, stalled], CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(clock.DueTime, Is.EqualTo(TimeSpan.FromSeconds(15)));
            clock.Expire();
            ISession session = await dial.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(session.RemoteAddress.ToString(), Is.EqualTo(valid.ToString()));
            Assert.That(transport.DialCount, Is.EqualTo(1));
            await session.DisconnectAsync();
        }
        finally
        {
            release.TrySetResult();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        clock.Timer.Received(1).Dispose();
    }

    [Test]
    public async Task ConnectionBudgetStartsAfterAddressResolution()
    {
        TimeSpan budget = TimeSpan.FromSeconds(2);
        TimeSpan guard = TimeSpan.FromSeconds(5);
        ManualTimeProvider clock = new();
        IDnsLookup dns = Substitute.For<IDnsLookup>();
        TaskCompletionSource queryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IEnumerable<string>> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        dns.QueryTxtAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            queryStarted.SetResult();
            return answer.Task.WaitAsync(call.Arg<CancellationToken>());
        });
        GateTransport transport = new();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport, resolver: new MultiaddrResolver(dns, clock),
            resourceLimits: new PeerResourceLimits(connectionTimeout: budget), clock: clock);
        Multiaddress literal = $"/ip4/127.0.0.1/tcp/4001/p2p/{TestPeers.PeerId(2)}";
        Multiaddress name = $"/dnsaddr/pending.test/p2p/{TestPeers.PeerId(2)}";
        using CancellationTokenSource caller = new();
        Task<ISession> dial = peer.DialAsync([literal, name], caller.Token);
        try
        {
            await queryStarted.Task.WaitAsync(guard);
            Assert.That(clock.ActiveTimersFor(budget), Is.Zero);
            clock.Advance(budget + TimeSpan.FromSeconds(1));
            Assert.That(transport.Started.Task.IsCompleted, Is.False);
            Assert.That(dial.IsCompleted, Is.False);

            answer.SetResult([$"dnsaddr={literal}"]);
            await transport.Started.Task.WaitAsync(guard);
            Assert.That(clock.ActiveTimersFor(TimeSpan.FromSeconds(15)), Is.Zero);
            Assert.That(clock.ActiveTimersFor(budget), Is.EqualTo(1));
            clock.Advance(budget);
            await Task.WhenAny(dial).WaitAsync(guard);
            Assert.ThrowsAsync<TimeoutException>(async () => await dial);
            await transport.Canceled.Task.WaitAsync(guard);
        }
        finally { caller.Cancel(); }
    }

    [Test]
    public async Task ExpiredAttemptCannotPublishAsConnectedDuringTransportStartup()
    {
        TimeSpan budget = TimeSpan.FromSeconds(2);
        TimeSpan guard = TimeSpan.FromSeconds(5);
        ManualTimeProvider clock = new();
        GateTransport transport = new()
        {
            BeforeDial = (_, _) =>
            {
                clock.Advance(budget);
                return Task.CompletedTask;
            }
        };
        transport.CompleteDial.SetResult();
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport,
            resourceLimits: new PeerResourceLimits(connectionTimeout: budget), clock: clock);
        bool published = false;
        peer.OnConnected += _ => published = true;

        Task<ISession> dial = peer.DialAsync(TestPeers.Multiaddr(2));
        await Task.WhenAny(dial).WaitAsync(guard);
        Assert.ThrowsAsync<TimeoutException>(async () => await dial);
        await transport.ConnectionClosed.Task.WaitAsync(guard);
        Assert.That(published, Is.False);
        Assert.That(peer.Sessions, Is.Empty);
    }

    [Test]
    public async Task SharedDeadlinePreventsPublishingReadinessWhileInitializationFinishes()
    {
        TimeSpan budget = TimeSpan.FromSeconds(2);
        TimeSpan guard = TimeSpan.FromSeconds(5);
        BlockingTimestampClock clock = new();
        GateTransport transport = new();
        TaskCompletionSource initializing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ControlledPeer peer = new(TestPeers.Identity(1), transport, () =>
        {
            initializing.SetResult();
            return initialization.Task;
        }, resourceLimits: new PeerResourceLimits(connectionTimeout: budget), clock: clock);
        TaskCompletionSource published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.OnConnected += _ => published.TrySetResult();

        // Post the dial continuations so transport cancellation can race with the
        // initializer without synchronously waiting for its lifecycle lock.
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<ISession> dial;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new PostingSynchronizationContext());
            dial = peer.DialAsync(TestPeers.Multiaddr(2));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        transport.CompleteDial.SetResult();
        await initializing.Task.WaitAsync(guard);
        ISession session = peer.Sessions.Single();
        Task expire = Task.CompletedTask;
        try
        {
            initialization.SetResult();
            await clock.TimestampRequested.Task.WaitAsync(guard);
            expire = Task.Run(() => clock.Advance(budget));
            await Task.WhenAny(dial).WaitAsync(guard);
            Assert.ThrowsAsync<TimeoutException>(async () => await dial);
        }
        finally
        {
            clock.ReleaseTimestamp.TrySetResult();
            await expire.WaitAsync(guard);
        }

        await transport.ConnectionClosed.Task.WaitAsync(guard);
        await session.DisconnectAsync().WaitAsync(guard);
        Assert.That(session.IsInitialized, Is.False);
        Assert.That(published.Task.IsCompleted, Is.False);
        Assert.That(peer.Sessions, Is.Empty);
    }

    [Test]
    [NonParallelizable]
    public async Task ReadyAttemptKeepsTransportWhenDeadlineFiresBeforeDialReturns()
    {
        TimeSpan budget = TimeSpan.FromSeconds(2);
        TimeSpan guard = TimeSpan.FromSeconds(5);
        ManualTimeProvider clock = new();
        GateTransport transport = new();
        TaskCompletionSource recordingDuration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseMeasurement = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Histogram<double> dialDuration = Libp2pMetrics.DialDuration;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument == dialDuration) meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) =>
        {
            recordingDuration.TrySetResult();
            releaseMeasurement.Task.GetAwaiter().GetResult();
        });
        listener.Start();

        await using ControlledPeer peer = new(TestPeers.Identity(1), transport,
            resourceLimits: new PeerResourceLimits(connectionTimeout: budget), clock: clock);
        Task<ISession> dial = peer.DialAsync(TestPeers.Multiaddr(2));
        ISession session;
        try
        {
            transport.CompleteDial.SetResult();
            await recordingDuration.Task.WaitAsync(guard);
            Assert.That(dial.IsCompleted, Is.False);
            clock.Advance(budget);
            session = await dial.WaitAsync(guard);
        }
        finally { releaseMeasurement.TrySetResult(); }

        Assert.That(session.IsInitialized, Is.True);
        Assert.That(session.Closed.IsCancellationRequested, Is.False);
        Assert.That(transport.PostUpgradeDialCanceled.Task.IsCompleted, Is.False);
        await session.DisconnectAsync();
        await transport.ConnectionClosed.Task.WaitAsync(guard);
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

    private sealed class ControlledPeer(Identity identity, ITransportProtocol? transport = null, Func<Task>? connectedTo = null, ActivitySource? activitySource = null, MultiaddrResolver? resolver = null, IPeerStore? store = null, ILoggerFactory? loggerFactory = null,
        PeerResourceLimits? resourceLimits = null, TimeProvider? clock = null)
        : LocalPeer(identity, store, new ProtocolStackSettings(), activitySource, loggerFactory: loggerFactory, multiaddrResolver: resolver, resourceLimits: resourceLimits, timeProvider: clock)
    {
        protected override ProtocolRef SelectProtocol(Multiaddress addr) => new(transport ?? new GateTransport());
        protected override Task ConnectedTo(ISession session, bool isDialer) => connectedTo?.Invoke() ?? Task.CompletedTask;
    }

    private sealed class ResolutionClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan? DueTime { get; private set; }
        public ITimer Timer { get; } = Substitute.For<ITimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return Timer;
        }

        public void Expire() => _callback!(_state);
    }

    private sealed class BlockingTimestampClock : TimeProvider
    {
        private readonly ManualTimeProvider _clock = new();
        public TaskCompletionSource TimestampRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseTimestamp { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override long GetTimestamp()
        {
            TimestampRequested.TrySetResult();
            ReleaseTimestamp.Task.GetAwaiter().GetResult();
            return base.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => _clock.CreateTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);
    }

    private sealed class PostingSynchronizationContext : SynchronizationContext;

    private sealed class BlockingFailureLogger : ILogger, ILoggerFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() => Release.Dispose();
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, exception).StartsWith("Disconnecting due to exception")) return;
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Failure-log barrier was not released.");
        }
    }

    private sealed class ThrowingTransport : ITransportProtocol
    {
        public string Id => "/throwing";
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [$"/p2p/{peerId}"];
        public static bool IsAddressMatch(Multiaddress addr) => true;
        public CancellationToken ConnectionToken { get; private set; }
        public Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
        {
            INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = remoteAddr;
            ConnectionToken = connection.Token;
            connection.UpgradeToSession();
            throw new InvalidOperationException("synchronous transport failure");
        }
        public Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token) => Task.CompletedTask;
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
        public Multiaddress? OnlyReachableAddress { get; init; }
        public Func<Multiaddress, CancellationToken, Task>? BeforeDial { get; init; }

        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [$"/p2p/{peerId}"];
        public static bool IsAddressMatch(Multiaddress addr) => true;

        public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
        {
            Interlocked.Increment(ref DialCount);
            Started.TrySetResult();
            if (OnlyReachableAddress is not null && remoteAddr.ToString() != OnlyReachableAddress.ToString())
            {
                throw new IOException("Address is unreachable");
            }
            try
            {
                if (BeforeDial is not null) await BeforeDial(remoteAddr, token);
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
