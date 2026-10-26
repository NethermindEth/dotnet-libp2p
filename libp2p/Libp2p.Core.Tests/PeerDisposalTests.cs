// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Metrics;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
[NonParallelizable]
internal class PeerDisposalTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExistingConnectionCannotUpgradeAfterPeerDisposal(bool listener)
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        await using DisposalPeer peer = new(store);
        LocalPeer.Session session = NewSession(peer, 2);
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, listener, null);
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        try
        {
            await peer.DisposeAsync();
            Exception? failure = CaptureFailure(() => connection.UpgradeToSession());

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
                Assert.That(metrics.SessionsOpened, Is.Zero);
            });
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SessionAdmissionDuringShutdownCannotEscapeTheSnapshot(bool listener)
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(2);
        await using DisposalPeer peer = new(store);
        LocalPeer.Session admitted = Admit(peer, 2);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        LocalPeer.Session late = NewSession(peer, 3);
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, late, listener, null);
        Exception? lateFailure = null;
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        using CancellationTokenRegistration registration = admitted.ConnectionToken.Register(() =>
            lateFailure = CaptureFailure(() => connection.UpgradeToSession()));
        try
        {
            await peer.DisposeAsync();

            Assert.Multiple(() =>
            {
                Assert.That(lateFailure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(late.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.EqualTo(1));
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(retained.Sessions, Is.Zero);
                Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(3), out _), Is.False);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
                Assert.That(metrics.SessionsOpened, Is.EqualTo(1));
                Assert.That(metrics.SessionsClosed, Is.EqualTo(1));
            });
        }
        finally
        {
            await late.DisconnectAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DialReleasedAfterShutdownCannotAdmitASession(bool beforeConnection)
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        AdmissionTransport transport = new(beforeConnection);
        await using DisposalPeer peer = new(store, transport);
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        Task<ISession> dial = peer.DialAsync(TestPeers.Multiaddr(2));
        ISession? lateSession = null;
        try
        {
            await (beforeConnection ? transport.Started.Task : transport.ConnectionCreated.Task).WaitAsync(Deadline);
            await peer.DisposeAsync().AsTask().WaitAsync(Deadline);
            transport.Release.TrySetResult();
            Exception? failure = await CaptureFailureAsync(async () => lateSession = await dial.WaitAsync(Deadline));

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.Not.Null);
                Assert.That(failure, Is.Not.InstanceOf<TimeoutException>());
                Assert.That(lateSession, Is.Null);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
            });
        }
        finally
        {
            transport.Release.TrySetResult();
            if (lateSession is not null) await lateSession.DisconnectAsync();
            await CaptureFailureAsync(async () => await (await dial.WaitAsync(Deadline)).DisconnectAsync());
            if (transport.Work is { } work) await CaptureFailureAsync(async () => await work.WaitAsync(Deadline));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposedPeerRejectsNewConnectionCreation(bool suppliedSession)
    {
        using ActiveMetrics metrics = new();
        await using DisposalPeer peer = new(new PeerStore(1));
        LocalPeer.Session session = NewSession(peer, 2);
        INewConnectionContext? accepted = null;
        try
        {
            await peer.DisposeAsync();
            Exception? failure = CaptureFailure(() => accepted = peer.CreateConnection(peer.TransportRef, suppliedSession ? session : null, true, null));

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ChannelClosedException>());
                if (suppliedSession) Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(metrics.ConnectionsOpened, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
            });
        }
        finally
        {
            // Clean up a connection accepted by the pre-fix implementation during the red run.
            if (accepted is not null && !suppliedSession)
            {
                accepted.State.RemoteAddress = TestPeers.Multiaddr(2);
                using INewSessionContext admitted = accepted.UpgradeToSession();
            }
            accepted?.Dispose();
            await session.DisconnectAsync();
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task DisposedPeerRejectsNewDialAndListenCallsBeforeStartingTransport(int call)
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        store.Discover([TestPeers.Multiaddr(2)]);
        AdmissionTransport transport = new(beforeConnection: true);
        transport.Release.TrySetResult();
        await using DisposalPeer peer = new(store, transport);
        ISession? lateSession = null;
        try
        {
            await peer.DisposeAsync();
            Exception? failure = await CaptureFailureAsync(async () =>
            {
                switch (call)
                {
                    case 0: lateSession = await peer.DialAsync(TestPeers.Multiaddr(2)); break;
                    case 1: lateSession = await peer.DialAsync([TestPeers.Multiaddr(2)], CancellationToken.None); break;
                    case 2: lateSession = await peer.DialAsync(TestPeers.PeerId(2)); break;
                    default: await peer.StartListenAsync([TestPeers.Multiaddr(1)]); break;
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ObjectDisposedException>());
                Assert.That(transport.Dials, Is.Zero);
                Assert.That(transport.Listeners, Is.Zero);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(metrics.ConnectionsOpened, Is.Zero);
                Assert.That(metrics.SessionsOpened, Is.Zero);
            });
        }
        finally
        {
            if (lateSession is not null) await lateSession.DisconnectAsync();
            if (transport.Work is { } work) await CaptureFailureAsync(async () => await work.WaitAsync(Deadline));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReentrantDisposalWaitsForActualSessionCleanup(bool disconnectFirst)
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        await using DisposalPeer peer = new(store);
        LocalPeer.Session session = Admit(peer, 2);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        Task? nestedDisconnect = null;
        Task? nestedDispose = null;
        Task? repeatedDispose = null;
        bool disconnectFinishedInCallback = true;
        bool disposeFinishedInCallback = true;
        using CancellationTokenRegistration registration = session.ConnectionToken.Register(() =>
        {
            nestedDisconnect = session.DisconnectAsync();
            nestedDispose = peer.DisposeAsync().AsTask();
            repeatedDispose = peer.DisposeAsync().AsTask();
            disconnectFinishedInCallback = nestedDisconnect.IsCompleted;
            disposeFinishedInCallback = nestedDispose.IsCompleted || repeatedDispose.IsCompleted;
        });

        Task outer = disconnectFirst ? session.DisconnectAsync() : peer.DisposeAsync().AsTask();
        await outer.WaitAsync(Deadline);
        Assert.That(nestedDisconnect, Is.Not.Null);
        Assert.That(nestedDispose, Is.Not.Null);
        Assert.That(repeatedDispose, Is.Not.Null);
        await Task.WhenAll(nestedDisconnect!, nestedDispose!, repeatedDispose!).WaitAsync(Deadline);
        Task laterDispose = peer.DisposeAsync().AsTask();
        await laterDispose.WaitAsync(Deadline);

        Assert.Multiple(() =>
        {
            Assert.That(disconnectFinishedInCallback, Is.False, "A reentrant disconnect must await the current cleanup.");
            Assert.That(disposeFinishedInCallback, Is.False, "Peer disposal must await release/removal after cancellation callbacks return.");
            Assert.That(nestedDispose, Is.SameAs(repeatedDispose));
            Assert.That(laterDispose, Is.SameAs(nestedDispose));
            if (disconnectFirst) Assert.That(nestedDisconnect, Is.SameAs(outer));
            else Assert.That(nestedDispose, Is.SameAs(outer));
            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.Empty);
            Assert.That(retained.Sessions, Is.Zero);
            Assert.That(metrics.Connections, Is.Zero);
            Assert.That(metrics.Sessions, Is.Zero);
            Assert.That(metrics.SessionsClosed, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task DisposalClosesEverySessionAndSharesFailureWithConcurrentRepeatCallers()
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(2);
        DisposalPeer peer = new(store);
        LocalPeer.Session first = Admit(peer, 2);
        LocalPeer.Session second = Admit(peer, 3);
        PeerStore.PeerInfo firstRetained = store.GetPeerInfo(TestPeers.PeerId(2));
        PeerStore.PeerInfo secondRetained = store.GetPeerInfo(TestPeers.PeerId(3));
        int secondClosed = 0;
        using CancellationTokenRegistration throwing = first.ConnectionToken.Register(() => throw new InvalidOperationException("Closed subscriber failed."));
        using CancellationTokenRegistration observing = second.ConnectionToken.Register(() => Interlocked.Increment(ref secondClosed));
        Task initialDisposal = peer.DisposeAsync().AsTask();
        Exception? firstFailure = await CaptureFailureAsync(async () => await initialDisposal.WaitAsync(Deadline));
        bool secondWasClosedByInitialDisposal = second.ConnectionToken.IsCancellationRequested;
        int remainingSessionsAfterInitialDisposal = peer.Sessions.Count;
        int secondPinsAfterInitialDisposal = secondRetained.Sessions;
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Task> firstCaller = RequestDisposalAsync();
        Task<Task> secondCaller = RequestDisposalAsync();
        start.TrySetResult();
        Task[] disposals = await Task.WhenAll(firstCaller, secondCaller).WaitAsync(Deadline);
        Exception? repeatedFailure = await CaptureFailureAsync(async () => await disposals[0].WaitAsync(Deadline));
        Exception? concurrentFailure = await CaptureFailureAsync(async () => await disposals[1].WaitAsync(Deadline));

        Assert.Multiple(() =>
        {
            Assert.That(disposals[0], Is.SameAs(initialDisposal));
            Assert.That(disposals[1], Is.SameAs(initialDisposal));
            Assert.That(firstFailure, Is.InstanceOf<AggregateException>());
            Assert.That(repeatedFailure, Is.SameAs(firstFailure));
            Assert.That(concurrentFailure, Is.SameAs(firstFailure));
            Assert.That(secondWasClosedByInitialDisposal, Is.True);
            Assert.That(remainingSessionsAfterInitialDisposal, Is.Zero);
            Assert.That(secondPinsAfterInitialDisposal, Is.Zero);
            Assert.That(first.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(second.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(secondClosed, Is.EqualTo(1));
            Assert.That(peer.Sessions, Is.Empty);
            Assert.That(firstRetained.Sessions, Is.Zero);
            Assert.That(secondRetained.Sessions, Is.Zero);
            Assert.That(metrics.Connections, Is.Zero);
            Assert.That(metrics.Sessions, Is.Zero);
            Assert.That(metrics.SessionsClosed, Is.EqualTo(2));
        });

        async Task<Task> RequestDisposalAsync()
        {
            await start.Task;
            return peer.DisposeAsync().AsTask();
        }
    }

    [Test]
    public async Task CompletingInitializationAfterDisposalDoesNotPublishAConnection()
    {
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PeerStore store = new(1);
        await using DisposalPeer peer = new(store, initialization: initialization.Task);
        LocalPeer.Session session = Admit(peer, 2);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        await peer.DisposeAsync();
        initialization.TrySetResult();
        Exception? failure = await CaptureFailureAsync(async () => await session.Connected.WaitAsync(Deadline));

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.Empty);
            Assert.That(retained.Sessions, Is.Zero);
            Assert.That(connectedEvents, Is.Zero);
        });
    }

    [Test]
    public async Task CloseCallbackCannotPublishAConnectionByCompletingInitializationInline()
    {
        // Inline completion exercises the initialization continuation inside the close callback.
        TaskCompletionSource initialization = new();
        PeerStore store = new(1);
        await using DisposalPeer peer = new(store, initialization: initialization.Task);
        LocalPeer.Session session = Admit(peer, 2);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        using CancellationTokenRegistration registration = session.ConnectionToken.Register(() => initialization.TrySetResult());

        await peer.DisposeAsync();
        Exception? failure = await CaptureFailureAsync(async () => await session.Connected.WaitAsync(Deadline));

        Assert.Multiple(() =>
        {
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(session.Connected.IsCanceled, Is.True);
            Assert.That(connectedEvents, Is.Zero);
            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.Empty);
            Assert.That(retained.Sessions, Is.Zero);
        });
    }

    [Test]
    public async Task DisposalFromSessionLoggingCannotPublishALiveSession()
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        Action? dispose = null;
        using CallbackLogger logger = new(() => dispose!());
        await using DisposalPeer peer = new(store, loggerFactory: logger);
        LocalPeer.Session session = NewSession(peer, 2);
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, false, null);
        Task? disposal = null;
        dispose = () => disposal = peer.DisposeAsync().AsTask();
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        try
        {
            Exception? failure = CaptureFailure(() => connection.UpgradeToSession());
            Assert.That(disposal, Is.Not.Null);
            await disposal!.WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(retained.Sessions, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
            });
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SessionOpeningInstrumentationCannotPublishAfterReentrantShutdown(bool disconnectSession)
    {
        Action? terminate = null;
        int callbacks = 0;
        using ActiveMetrics metrics = new((instrument, _) =>
        {
            if (instrument == Libp2pMetrics.SessionsOpened && Interlocked.Increment(ref callbacks) == 1)
                terminate!();
        });
        PeerStore store = new(1);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        await using DisposalPeer peer = new(store);
        LocalPeer.Session session = NewSession(peer, 2);
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, false, null);
        Task? termination = null;
        bool finishedInsideInstrumentation = true;
        terminate = () =>
        {
            termination = disconnectSession ? session.DisconnectAsync() : peer.DisposeAsync().AsTask();
            finishedInsideInstrumentation = termination.IsCompleted;
        };
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        try
        {
            Exception? failure = CaptureFailure(() => connection.UpgradeToSession());
            Assert.That(termination, Is.Not.Null);
            await termination!.WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(finishedInsideInstrumentation, Is.False);
                Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(retained.Sessions, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
                Assert.That(metrics.SessionsOpened, Is.EqualTo(1));
                Assert.That(metrics.SessionsClosed, Is.EqualTo(1));
            });
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConnectionOpeningInstrumentationCannotReturnAConnectionAfterReentrantShutdown(bool disconnectSession)
    {
        Action? terminate = null;
        int callbacks = 0;
        using ActiveMetrics metrics = new((instrument, _) =>
        {
            if (instrument == Libp2pMetrics.ConnectionsOpened && Interlocked.Increment(ref callbacks) == 1)
                terminate!();
        });
        await using DisposalPeer peer = new(new PeerStore(1));
        LocalPeer.Session session = NewSession(peer, 2);
        Task? termination = null;
        bool finishedInsideInstrumentation = true;
        terminate = () =>
        {
            termination = disconnectSession ? session.DisconnectAsync() : peer.DisposeAsync().AsTask();
            finishedInsideInstrumentation = termination.IsCompleted;
        };
        INewConnectionContext? accepted = null;
        try
        {
            Exception? failure = CaptureFailure(() => accepted = peer.CreateConnection(peer.TransportRef, session, false, null));
            Assert.That(termination, Is.Not.Null);
            await termination!.WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(finishedInsideInstrumentation, Is.False);
                Assert.That(accepted, Is.Null);
                Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.ConnectionsOpened, Is.EqualTo(1));
                Assert.That(metrics.SessionsOpened, Is.Zero);
            });
        }
        finally
        {
            accepted?.Dispose();
            await session.DisconnectAsync();
        }
    }

    [Test]
    public async Task DisposalFromSessionCollectionNotificationPreventsInitialization()
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        await using DisposalPeer peer = new(store);
        LocalPeer.Session session = NewSession(peer, 2);
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, false, null);
        Task? disposal = null;
        peer.Sessions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add)
                disposal = peer.DisposeAsync().AsTask();
        };
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        try
        {
            Exception? failure = CaptureFailure(() => connection.UpgradeToSession());
            Assert.That(disposal, Is.Not.Null);
            await disposal!.WaitAsync(Deadline);

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(retained.Sessions, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
            });
        }
        finally
        {
            await session.DisconnectAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TwoCollectionSubscribersCanCloseDuringAdmissionWithoutLeavingStaleMembership(bool disposePeer)
    {
        using ActiveMetrics metrics = new();
        PeerStore store = new(1);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        DisposalPeer peer = new(store);
        LocalPeer.Session session = NewSession(peer, 2);
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, false, null);
        Task? termination = null;
        bool finishedInsideNotification = true;
        peer.Sessions.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Add && args.NewItems!.Contains(session))
            {
                termination = disposePeer ? peer.DisposeAsync().AsTask() : session.DisconnectAsync();
                finishedInsideNotification = termination.IsCompleted;
            }
        };
        peer.Sessions.CollectionChanged += (_, _) => { };
        int connectedEvents = 0;
        peer.OnConnected += _ => connectedEvents++;
        try
        {
            Exception? admissionFailure = CaptureFailure(() => connection.UpgradeToSession());
            Assert.That(termination, Is.Not.Null);
            Exception? cleanupFailure = await CaptureFailureAsync(async () => await termination!.WaitAsync(Deadline));

            Assert.Multiple(() =>
            {
                Assert.That(admissionFailure, Is.InstanceOf<ChannelClosedException>());
                Assert.That(cleanupFailure, Is.Null);
                Assert.That(finishedInsideNotification, Is.False);
                Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(peer.Initializations, Is.Zero);
                Assert.That(connectedEvents, Is.Zero);
                Assert.That(retained.Sessions, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
            });

            if (!disposePeer)
            {
                LocalPeer.Session replacement = NewSession(peer, 2);
                try
                {
                    using INewConnectionContext replacementConnection = peer.CreateConnection(peer.TransportRef, replacement, false, null);
                    using INewSessionContext replacementContext = replacementConnection.UpgradeToSession();
                    await replacement.Connected.WaitAsync(Deadline);
                    Assert.Multiple(() =>
                    {
                        Assert.That(peer.Sessions, Is.EqualTo(new[] { replacement }));
                        Assert.That(replacement.ConnectionToken.IsCancellationRequested, Is.False);
                        Assert.That(retained.Sessions, Is.EqualTo(1));
                        Assert.That(peer.Initializations, Is.EqualTo(1));
                        Assert.That(connectedEvents, Is.EqualTo(1));
                    });
                }
                finally
                {
                    await replacement.DisconnectAsync();
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(retained.Sessions, Is.Zero);
                Assert.That(metrics.Connections, Is.Zero);
                Assert.That(metrics.Sessions, Is.Zero);
            });
        }
        finally
        {
            await CaptureFailureAsync(() => session.DisconnectAsync());
            await CaptureFailureAsync(() => peer.DisposeAsync().AsTask());
        }
    }

    private static LocalPeer.Session NewSession(LocalPeer peer, int remote)
        => new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(remote) } };

    private static LocalPeer.Session Admit(DisposalPeer peer, int remote)
    {
        LocalPeer.Session session = NewSession(peer, remote);
        peer.CreateConnection(peer.TransportRef, session, false, null);
        peer.UpgradeToSession(session, peer.TransportRef, false, null);
        return session;
    }

    private static Exception? CaptureFailure(Action action)
    {
        try { action(); return null; }
        catch (Exception failure) { return failure; }
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception failure) { return failure; }
    }

    private sealed class DisposalPeer(PeerStore store, AdmissionTransport? transport = null, Task? initialization = null, ILoggerFactory? loggerFactory = null)
        : LocalPeer(TestPeers.Identity(1), store, new ProtocolStackSettings(), loggerFactory: loggerFactory)
    {
        private int _initializations;
        public int Initializations => Volatile.Read(ref _initializations);
        public ProtocolRef TransportRef { get; } = new(transport ?? new AdmissionTransport(beforeConnection: true));
        protected override ProtocolRef SelectProtocol(Multiaddress address) => TransportRef;
        protected override Task ConnectedTo(ISession session, bool isDialer)
        {
            Interlocked.Increment(ref _initializations);
            return initialization ?? Task.CompletedTask;
        }
    }

    private sealed class CallbackLogger(Action callback) : ILogger, ILoggerFactory
    {
        private int _called;
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("New session with", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _called, 1) == 0)
                callback();
        }
    }

    private sealed class AdmissionTransport(bool beforeConnection) : ITransportProtocol
    {
        private int _dials;
        private int _listeners;
        public string Id => "/disposal-test";
        public int Dials => Volatile.Read(ref _dials);
        public int Listeners => Volatile.Read(ref _listeners);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConnectionCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? Work { get; private set; }
        public static bool IsAddressMatch(Multiaddress address) => true;
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [$"/p2p/{peerId}"];

        public Task DialAsync(ITransportContext context, Multiaddress remoteAddress, CancellationToken token)
            => Work = DialCoreAsync(context, remoteAddress);

        private async Task DialCoreAsync(ITransportContext context, Multiaddress remoteAddress)
        {
            Interlocked.Increment(ref _dials);
            Started.TrySetResult();
            // Deliberately ignore the caller token: terminal session admission must be authoritative.
            if (beforeConnection) await Release.Task;
            using INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = remoteAddress;
            ConnectionCreated.TrySetResult();
            if (!beforeConnection) await Release.Task;
            using INewSessionContext session = connection.UpgradeToSession();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, connection.Token); }
            catch (OperationCanceledException) when (connection.Token.IsCancellationRequested) { }
        }

        public Task ListenAsync(ITransportContext context, Multiaddress listenAddress, CancellationToken token)
        {
            Interlocked.Increment(ref _listeners);
            context.ListenerReady(listenAddress);
            return Task.CompletedTask;
        }
    }

    private sealed class ActiveMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _connections;
        private long _sessions;
        private long _connectionsOpened;
        private long _sessionsOpened;
        private long _sessionsClosed;
        public long Connections => Interlocked.Read(ref _connections);
        public long Sessions => Interlocked.Read(ref _sessions);
        public long ConnectionsOpened => Interlocked.Read(ref _connectionsOpened);
        public long SessionsOpened => Interlocked.Read(ref _sessionsOpened);
        public long SessionsClosed => Interlocked.Read(ref _sessionsClosed);

        public ActiveMetrics(Action<Instrument, long>? observation = null)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter == Libp2pMetrics.Meter) listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument == Libp2pMetrics.ConnectionsActive) Interlocked.Add(ref _connections, value);
                if (instrument == Libp2pMetrics.SessionsActive) Interlocked.Add(ref _sessions, value);
                if (instrument == Libp2pMetrics.ConnectionsOpened) Interlocked.Add(ref _connectionsOpened, value);
                if (instrument == Libp2pMetrics.SessionsOpened) Interlocked.Add(ref _sessionsOpened, value);
                if (instrument == Libp2pMetrics.SessionsClosed) Interlocked.Add(ref _sessionsClosed, value);
                observation?.Invoke(instrument, value);
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }
}
