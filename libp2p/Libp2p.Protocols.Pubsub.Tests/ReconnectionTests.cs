// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

public class ReconnectionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ClosedInboundTransportWithoutKnownAddressesDoesNotDialItsSourcePort()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        ILocalPeer local = CreateLocal();
        await router.StartAsync(local);
        Multiaddress source = Address(1, 50000);
        TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (CancellationToken closed, _) = router.InboundConnection(source, PubsubRouter.FloodsubProtocolVersion,
            stream.Task, () => Task.CompletedTask, () => Task.CompletedTask, ConnectionDirection.Inbound);
        await CloseAsync(stream, closed);

        router.Reconnect(CancellationToken.None);

        await local.DidNotReceive().DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RecoveryWaitsForAnOverloadedSessionToDisconnect()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        ILocalPeer local = CreateLocal();
        await router.StartAsync(local);
        router.GetTopic("recover");
        TaskCompletionSource abortStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource abortDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disconnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disconnectDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Multiaddress address = Address(1, 10001);
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion,
            stream.Task, (_, _) => ValueTask.FromResult(IOResult.InternalError),
            () => { abortStarted.TrySetResult(); return new(abortDone.Task); },
            () => { disconnectStarted.TrySetResult(); return disconnectDone.Task; }, ConnectionDirection.Outbound)!;

        try
        {
            await abortStarted.Task.WaitAsync(TestTimeout);
            router.Reconnect(CancellationToken.None);
            await local.DidNotReceive().DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());

            abortDone.SetResult();
            await disconnectStarted.Task.WaitAsync(TestTimeout);
            router.Reconnect(CancellationToken.None);
            await local.DidNotReceive().DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            abortDone.TrySetResult();
            disconnectDone.TrySetResult();
            stream.TrySetResult();
            await peer.Teardown.WaitAsync(TestTimeout);
        }

        router.Reconnect(CancellationToken.None);
        await local.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RetryKeepsItsBudgetWhenThePeerGenerationExpiresDuringEstablishment()
    {
        PeerStore store = new();
        Multiaddress address = Address(1, 10001);
        PeerId id = address.GetPeerId()!;
        store.SetSupportedProtocols(id, [PubsubRouter.FloodsubProtocolVersion]);
        ISession session = Substitute.For<ISession>();
        session.RemotePeerId.Returns(id);
        ILocalPeer local = CreateLocal();
        TaskCompletionSource<ISession> transport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ISession> failedDial = Task.FromException<ISession>(new IOException("Connection failed"));
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>())
            .Returns(failedDial, transport.Task, failedDial);
        TaskCompletionSource teardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disconnectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource disconnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RetryClock clock = new(teardown.Task);
        PubsubSettings settings = Settings();
        await using PubsubRouter router = new(store, settings, timeProvider: clock);
        await router.StartAsync(local);

        try
        {
            store.Discover([address]);
            router.Reconnect(CancellationToken.None);
            Task establishing = router.PendingConnections;

            router.InboundConnection(address, PubsubRouter.FloodsubProtocolVersion, incoming.Task,
                () => Task.FromException(new IOException("Reply failed")),
                () => { disconnectStarted.TrySetResult(); return disconnect.Task; }, ConnectionDirection.Outbound);
            router.GetTopic("recovery");
            Assert.That(router.OutboundUsage.Entries, Is.EqualTo(1));
            clock.Advance(TimeSpan.FromMilliseconds(settings.OutboundRpcTimeout));
            Assert.That(router.OutboundUsage.Entries, Is.Zero);

            transport.SetResult(session);
            await establishing.WaitAsync(TestTimeout);
            teardown.SetResult();
            await disconnectStarted.Task.WaitAsync(TestTimeout);
            router.Reconnect(CancellationToken.None);
            await local.Received(2).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());

            disconnect.SetResult();
            await router.PendingConnections.WaitAsync(TestTimeout);
            router.Reconnect(CancellationToken.None);
            await local.Received(3).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            transport.TrySetResult(session);
            incoming.TrySetResult();
            teardown.TrySetResult();
            disconnect.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RetryPrefersAdvertisedAddressesAndRetainsTheOutboundFallback(bool advertisedIncludesFallback)
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store, Settings());
        ILocalPeer local = CreateLocal();
        await router.StartAsync(local);
        Multiaddress outbound = Address(1, 10001), advertised = Address(1, 10002);
        TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken closed = router.OutboundConnection(outbound, PubsubRouter.FloodsubProtocolVersion,
            stream.Task, _ => { }, ConnectionDirection.Outbound);
        store.Discover(advertisedIncludesFallback ? [advertised, outbound] : [advertised]);

        await CloseAsync(stream, closed);
        router.Reconnect(CancellationToken.None);

        await local.Received(1).DialAsync(Arg.Is<Multiaddress[]>(addresses =>
            addresses.Length == 2 && addresses[0] == advertised && addresses[1] == outbound), Arg.Any<CancellationToken>());
    }

    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, false)]
    public async Task RetryUsesOnlyAddressesForItsPeer(bool hasOutboundFallback, bool hasAdvertisedAddress)
    {
        Multiaddress endpoint = Address(1, 10001), advertised = Address(1, 10002), otherPeer = Address(2, 10003);
        PeerId id = endpoint.GetPeerId()!;
        IPeerStore store = Substitute.For<IPeerStore>();
        PeerMetadata metadata = new(hasAdvertisedAddress ? [otherPeer, advertised] : [otherPeer],
            [PubsubRouter.FloodsubProtocolVersion]);
        store.TryGetPeerInfo(id, out Arg.Any<PeerMetadata?>()).Returns(call =>
        {
            call[1] = metadata;
            return true;
        });
        ISession session = Substitute.For<ISession>();
        session.RemotePeerId.Returns(id);
        session.IsInitialized.Returns(true);
        session.DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ILocalPeer local = CreateLocal();
        local.Sessions.Returns([session]);
        await using PubsubRouter router = new(store, Settings());
        await router.StartAsync(local);

        TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken closed = hasOutboundFallback
            ? router.OutboundConnection(endpoint, PubsubRouter.FloodsubProtocolVersion, stream.Task, _ => { }, ConnectionDirection.Outbound)
            : router.InboundConnection(endpoint, PubsubRouter.FloodsubProtocolVersion, stream.Task,
                () => Task.CompletedTask, () => Task.CompletedTask, ConnectionDirection.Inbound).Token;
        await CloseAsync(stream, closed);
        router.Reconnect(CancellationToken.None);

        if (hasOutboundFallback)
        {
            Multiaddress[] expected = hasAdvertisedAddress ? [advertised, endpoint] : [endpoint];
            await local.Received(1).DialAsync(Arg.Is<Multiaddress[]>(addresses => addresses.SequenceEqual(expected)), Arg.Any<CancellationToken>());
        }
        else
        {
            await local.DidNotReceive().DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
            await session.Received(1).DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task SameSessionRecoveryUsesTheAuthenticatedPeerId()
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store, Settings());
        ILocalPeer local = CreateLocal();
        Multiaddress source = Address(1, 50000);
        PeerId id = source.GetPeerId()!;
        ISession session = Substitute.For<ISession>();
        session.RemotePeerId.Returns(id);
        session.RemoteAddress.Returns((Multiaddress)"/ip4/127.0.0.1/tcp/50000");
        session.IsInitialized.Returns(true);
        session.DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        local.Sessions.Returns(new[] { session });
        store.SetSupportedProtocols(id, [PubsubRouter.FloodsubProtocolVersion]);
        await router.StartAsync(local);
        TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (CancellationToken closed, _) = router.InboundConnection(source, PubsubRouter.FloodsubProtocolVersion,
            stream.Task, () => Task.CompletedTask, () => Task.CompletedTask, ConnectionDirection.Inbound);

        await CloseAsync(stream, closed);
        router.Reconnect(CancellationToken.None);

        await session.Received(1).DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CancelingTheRouterDoesNotPromoteADeferredReply()
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store, Settings());
        ILocalPeer local = CreateLocal();
        Multiaddress address = Address(1, 10001);
        PeerId id = address.GetPeerId()!;
        store.SetSupportedProtocols(id, [PubsubRouter.FloodsubProtocolVersion]);
        ISession session = Substitute.For<ISession>();
        session.RemotePeerId.Returns(id);
        session.RemoteAddress.Returns(address);
        TaskCompletionSource dialing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>()).Returns(call =>
        {
            dialing.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>());
        });
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(session));
        using CancellationTokenSource lifetime = new();
        await router.StartAsync(local, lifetime.Token);
        TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken closed = router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion,
            stream.Task, _ => { }, ConnectionDirection.Outbound);
        await CloseAsync(stream, closed);
        router.Reconnect(CancellationToken.None);
        await dialing.Task.WaitAsync(TestTimeout);
        Task attempt = router.PendingConnections;

        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int replies = 0;
        router.InboundConnection(address, PubsubRouter.FloodsubProtocolVersion, incoming.Task, () =>
        {
            Interlocked.Increment(ref replies);
            return Task.CompletedTask;
        }, () => Task.CompletedTask, ConnectionDirection.Inbound);
        try
        {
            await lifetime.CancelAsync();
            await attempt.WaitAsync(TestTimeout);
            Assert.That(Volatile.Read(ref replies), Is.Zero);
        }
        finally { incoming.TrySetResult(); }
    }

    [Test]
    public async Task RetriesRemainBoundedAcrossBatchesAndCarryOverWaitingPeers()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        ILocalPeer local = CreateLocal();
        List<Multiaddress> attempted = [];
        TaskCompletionSource<ISession> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            attempted.Add(call.Arg<Multiaddress[]>()[0]);
            return failure.Task;
        });
        await router.StartAsync(local);
        Multiaddress[] addresses = Enumerable.Range(1, 7).Select(i => Address(i, 10000 + i)).ToArray();
        foreach (Multiaddress address in addresses)
        {
            TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken closed = router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion,
                stream.Task, (_, _) => ValueTask.FromResult(IOResult.Ok), () => ValueTask.CompletedTask,
                () => Task.CompletedTask, ConnectionDirection.Outbound)!.TokenSource.Token;
            await CloseAsync(stream, closed);
        }

        try
        {
            router.Reconnect(CancellationToken.None);
            Assert.That(attempted, Has.Count.EqualTo(5));
            router.Reconnect(CancellationToken.None);
            Assert.That(attempted, Has.Count.EqualTo(5), "retry work from the first batch is still in flight");
        }
        finally
        {
            failure.TrySetException(new IOException("Connection failed"));
            await router.PendingConnections;
        }

        router.Reconnect(CancellationToken.None);
        Assert.That(attempted, Is.EquivalentTo(addresses));
    }

    [Test]
    public async Task AddressUpdatesRefreshAnActiveRetryWithoutResettingItsBudget()
    {
        PeerStore store = new();
        PubsubSettings settings = Settings();
        settings.ReconnectionAttempts = 2;
        await using PubsubRouter router = new(store, settings);
        ILocalPeer local = CreateLocal();
        await router.StartAsync(local);
        Multiaddress first = Address(1, 10001), updated = Address(1, 10002);
        List<Multiaddress> attempted = [];
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            attempted.Add(call.Arg<Multiaddress[]>()[0]);
            return Task.FromException<ISession>(new IOException("Connection failed"));
        });

        store.Discover([first]);
        router.Reconnect(CancellationToken.None);
        store.Discover([updated]);
        for (int i = 0; i < 3; i++) router.Reconnect(CancellationToken.None);

        Assert.That(attempted, Is.EqualTo(new[] { first, first, updated }));
    }

    [Test]
    public async Task LateTransportDoesNotBypassThePolicyForANewRecovery()
    {
        PeerStore store = new();
        Multiaddress address = Address(1, 10001);
        PeerId id = address.GetPeerId()!;
        store.SetSupportedProtocols(id, [PubsubRouter.FloodsubProtocolVersion]);
        ISession session = Substitute.For<ISession>();
        session.RemotePeerId.Returns(id);
        session.DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        ILocalPeer local = CreateLocal();
        TaskCompletionSource<ISession> pendingTransport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>())
            .Returns(pendingTransport.Task, Task.FromResult(session));
        await using PubsubRouter router = new(store, Settings());
        await router.StartAsync(local);

        try
        {
            await CloseWriterAsync();
            router.Reconnect(CancellationToken.None);
            await local.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
            Task superseded = router.PendingConnections;

            await CloseWriterAsync();
            pendingTransport.SetResult(session);
            await superseded.WaitAsync(TestTimeout);

            await session.DidNotReceive().DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>());
            router.Reconnect(CancellationToken.None);
            await session.Received(1).DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>());
        }
        finally { pendingTransport.TrySetResult(session); }

        async Task CloseWriterAsync()
        {
            TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken closed = router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion,
                stream.Task, _ => { }, ConnectionDirection.Outbound);
            await CloseAsync(stream, closed);
        }
    }

    [Test]
    public async Task EvictedPeerCandidatesCannotGrowTheRetryBacklogPastItsLimit()
    {
        const int maxRecoveries = 10_000;
        PeerStore store = new(maxPeers: 1);
        TaskCompletionSource planning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        int planned = 0;
        Policy policy = new(_ =>
        {
            if (Interlocked.Increment(ref planned) == 1)
            {
                planning.TrySetResult();
                release.Wait(TestTimeout);
            }
            return null;
        });
        await using PubsubRouter router = new(store, Settings(), reconnectPolicy: policy);
        await router.StartAsync(CreateLocal());
        store.Discover([ChurnAddress(1)], isTrusted: false);
        try
        {
            await planning.Task.WaitAsync(TestTimeout);
            for (int i = 2; i <= maxRecoveries + 1; i++) store.Discover([ChurnAddress(i)], isTrusted: false);
        }
        finally { release.Set(); }

        router.Reconnect(CancellationToken.None);
        Assert.That(Volatile.Read(ref planned), Is.EqualTo(maxRecoveries));

        store.Discover([ChurnAddress(maxRecoveries + 2)], isTrusted: false);
        router.Reconnect(CancellationToken.None);
        Assert.That(Volatile.Read(ref planned), Is.EqualTo(maxRecoveries + 1));

        static Multiaddress ChurnAddress(int sequence)
        {
            byte[] hash = new byte[34];
            hash[0] = 0x12;
            hash[1] = 32;
            BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(2), sequence);
            return $"/ip4/127.0.0.1/tcp/10001/p2p/{new PeerId(hash)}";
        }
    }

    [Test]
    public async Task CustomPolicyChoosesAMonotonicDelayAndCanStopRecovery()
    {
        PeerStore store = new();
        RetryClock clock = new();
        PubsubSettings settings = Settings();
        settings.ReconnectionAttempts = 3;
        TimeSpan delay = TimeSpan.FromSeconds(7);
        ConcurrentQueue<PubsubReconnectAttempt> decisions = new();
        TaskCompletionSource declined = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PubsubRouter? routingLock = null;
        bool heldRoutingLock = false;
        Policy policy = new(attempt =>
        {
            heldRoutingLock |= Monitor.IsEntered(routingLock!);
            decisions.Enqueue(attempt);
            if (attempt.AttemptNumber == 1) return delay;
            declined.TrySetResult();
            return null;
        });
        await using PubsubRouter router = new(store, settings, timeProvider: clock, reconnectPolicy: policy);
        routingLock = router;
        ILocalPeer local = CreateLocal();
        await router.StartAsync(local);
        Multiaddress address = Address(1, 10001);

        store.Discover([address]);
        await clock.WaitForDelay(delay);
        await local.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        clock.UtcOffset = TimeSpan.FromHours(-48);
        clock.Advance(delay);
        await declined.Task.WaitAsync(TestTimeout);
        router.Reconnect(CancellationToken.None);

        Assert.That(heldRoutingLock, Is.False);
        Assert.That(decisions, Is.EqualTo(new[]
        {
            new PubsubReconnectAttempt(address.GetPeerId()!, PubsubReconnectReason.DiscoveryDialFailed, 1),
            new PubsubReconnectAttempt(address.GetPeerId()!, PubsubReconnectReason.DiscoveryDialFailed, 2),
        }));
        await local.Received(2).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WriterAttachmentReleasesSlotsWhileTheRecoveredStreamsRemainTracked()
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store, Settings());
        ILocalPeer local = CreateLocal();
        await router.StartAsync(local);
        TaskCompletionSource attach = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource keepStreamOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstBatchAttached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dials = 0, attached = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            Interlocked.Increment(ref dials);
            Multiaddress address = call.Arg<Multiaddress[]>()[0];
            ISession session = Substitute.For<ISession>();
            session.RemoteAddress.Returns(address);
            session.RemotePeerId.Returns(address.GetPeerId()!);
            session.DialAsync<FloodsubProtocol>(Arg.Any<CancellationToken>()).Returns(_ => AttachWriter(address));
            return session;
        });
        try
        {
            for (int i = 1; i <= 6; i++)
            {
                Multiaddress address = Address(i, 10000 + i);
                store.SetSupportedProtocols(address.GetPeerId()!, [PubsubRouter.FloodsubProtocolVersion]);
                TaskCompletionSource stream = new(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken closed = router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion,
                    stream.Task, _ => { }, ConnectionDirection.Outbound);
                await CloseAsync(stream, closed);
            }

            router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref dials), Is.EqualTo(5));
            attach.SetResult();
            await firstBatchAttached.Task.WaitAsync(TestTimeout);
            router.Reconnect(CancellationToken.None);

            Assert.That(Volatile.Read(ref dials), Is.EqualTo(6));
            Assert.That(router.PendingConnections.IsCompleted, Is.False);
        }
        finally
        {
            attach.TrySetResult();
            keepStreamOpen.TrySetResult();
        }

        async Task AttachWriter(Multiaddress address)
        {
            await attach.Task;
            router.OutboundConnection(address, PubsubRouter.FloodsubProtocolVersion, keepStreamOpen.Task,
                _ => { }, ConnectionDirection.Outbound);
            if (Interlocked.Increment(ref attached) == 5) firstBatchAttached.TrySetResult();
            await keepStreamOpen.Task;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ShutdownCancelsActiveRetriesAndDropsDelayedCandidates(bool cancelStartToken)
    {
        PeerStore store = new();
        RetryClock clock = new();
        Multiaddress active = Address(1, 10001), queued = Address(2, 10002);
        TimeSpan delay = TimeSpan.FromMinutes(5);
        Policy policy = new(attempt => attempt.PeerId == active.GetPeerId() ? TimeSpan.Zero : delay);
        using CancellationTokenSource lifetime = new();
        await using PubsubRouter router = new(store, Settings(), timeProvider: clock, reconnectPolicy: policy);
        ILocalPeer local = CreateLocal();
        TaskCompletionSource retryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource retryCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeDials = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<Multiaddress[]>()[0] == active && Interlocked.Increment(ref activeDials) == 2
                ? WaitForCancellation(call.Arg<CancellationToken>()) : Task.FromException<ISession>(new IOException("Connection failed")));
        await router.StartAsync(local, lifetime.Token);

        store.Discover([active]);
        await retryEntered.Task.WaitAsync(TestTimeout);
        store.Discover([queued]);
        await clock.WaitForDelay(delay);
        if (cancelStartToken) await lifetime.CancelAsync();
        else await router.DisposeAsync();

        await retryCanceled.Task.WaitAsync(TestTimeout);
        clock.Advance(delay);
        router.Reconnect(CancellationToken.None);
        store.Discover([Address(3, 10003)]);
        await local.Received(3).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());

        async Task<ISession> WaitForCancellation(CancellationToken token)
        {
            retryEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { retryCanceled.TrySetResult(); }
            throw new InvalidOperationException("The retry was not cancelled.");
        }
    }

    private static PubsubSettings Settings() => new()
    {
        HeartbeatInterval = int.MaxValue,
        ReconnectionPeriod = int.MaxValue,
        ReconnectionAttempts = 1,
    };

    private static ILocalPeer CreateLocal()
    {
        ILocalPeer local = Substitute.For<ILocalPeer>();
        local.Identity.Returns(TestPeers.Identity(0));
        local.ListenAddresses.Returns([]);
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ISession>(new IOException("Connection failed")));
        return local;
    }

    private static Multiaddress Address(int peer, int port) => $"/ip4/127.0.0.1/tcp/{port}/p2p/{TestPeers.PeerId(peer)}";

    private static async Task CloseAsync(TaskCompletionSource stream, CancellationToken closed)
    {
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = closed.Register(() => removed.TrySetResult());
        stream.SetResult();
        await removed.Task.WaitAsync(TestTimeout);
    }

    private sealed class Policy(Func<PubsubReconnectAttempt, TimeSpan?> decide) : IPubsubReconnectPolicy
    {
        public TimeSpan? GetRetryDelay(PubsubReconnectAttempt attempt) => decide(attempt);
    }

    private sealed class RetryClock(Task? beforeAsyncDisposal = null) : TimeProvider
    {
        private readonly ManualTimeProvider _clock = new();
        private readonly Channel<TimeSpan> _delays = global::System.Threading.Channels.Channel.CreateUnbounded<TimeSpan>();
        public TimeSpan UtcOffset { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _clock.GetUtcNow().Ticks;
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow() + UtcOffset;
        public void Advance(TimeSpan elapsed) => _clock.Advance(elapsed);

        public async Task WaitForDelay(TimeSpan expected)
        {
            using CancellationTokenSource timeout = new(TestTimeout);
            while (await _delays.Reader.ReadAsync(timeout.Token) != expected) { }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new RecordingTimer(_clock.CreateTimer(callback, state, dueTime, period), _delays.Writer, beforeAsyncDisposal);

        private sealed class RecordingTimer(ITimer timer, ChannelWriter<TimeSpan> delays, Task? beforeAsyncDisposal) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (!timer.Change(dueTime, period)) return false;
                if (dueTime > TimeSpan.Zero) delays.TryWrite(dueTime);
                return true;
            }

            public void Dispose() => timer.Dispose();
            public async ValueTask DisposeAsync()
            {
                if (beforeAsyncDisposal is not null) await beforeAsyncDisposal;
                await timer.DisposeAsync();
            }
        }
    }
}
