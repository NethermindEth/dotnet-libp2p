// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(5_000)]
public class OutboundConnectionRaceTests
{
    [TestCase(true, 2, false, false)]
    [TestCase(true, 0, false, true)]
    [TestCase(false, 0, false, false)]
    [TestCase(false, 1, true, false)]
    public async Task FailedOutgoingNegotiationResumesItsDeferredIncomingReply(
        bool directPeer, int retries, bool exhaustRetries, bool cancelNegotiation)
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        PeerStore store = new();
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            DirectPeers = directPeer ? [address] : [],
            ReconnectionAttempts = retries,
            ReconnectionPeriod = int.MaxValue,
            DirectConnectPeriod = int.MaxValue,
            HeartbeatInterval = int.MaxValue,
        });
        router.GetTopic("topic");
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        ILocalPeer local = CreateLocal(session);
        int physicalDials = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
            exhaustRetries && Interlocked.Increment(ref physicalDials) == 1
                ? Task.FromException<ISession>(new IOException("Connection failed"))
                : Task.FromResult(session));
        TaskCompletionSource negotiation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Rpc> sent = [];
        int replies = 0;
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(negotiation.Task);
        try
        {
            await router.StartAsync(local);
            if (!directPeer) store.Discover([address]);
            if (exhaustRetries) router.Reconnect(CancellationToken.None);
            Task initialWork = PendingWork(router);
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task, () =>
            {
                Interlocked.Increment(ref replies);
                router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, writer.Task, sent.Add);
                ready.TrySetResult();
                return reply.Task;
            });
            Assert.That(Volatile.Read(ref replies), Is.Zero);

            if (cancelNegotiation) negotiation.SetCanceled();
            else negotiation.SetException(new IOException("Negotiation failed"));
            await ready.Task.WaitAsync(token);
            await initialWork.WaitAsync(token);
            Assert.That(Volatile.Read(ref replies), Is.EqualTo(1));
            await router.FlushOutboundAsync();
            Assert.That(sent.SelectMany(rpc => rpc.Subscriptions)
                .Any(subscription => subscription.Topicid == "topic" && subscription.Subscribe), Is.True);

            router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics(["topic"], []));
            await router.Heartbeat();
            router.Publish("topic", [42]);
            await router.FlushOutboundAsync();
            Assert.That(sent.SelectMany(rpc => rpc.Publish).Any(message => message.Data.Span.SequenceEqual(new byte[] { 42 })), Is.True);
            router.Reconnect(CancellationToken.None);
            await local.Received(exhaustRetries ? 2 : 1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            negotiation.TrySetResult();
            incoming.TrySetResult();
            reply.TrySetResult();
            writer.TrySetResult();
        }
    }

    [TestCase("closed")]
    [TestCase("suppressed")]
    [TestCase("stopped")]
    public async Task DeferredReplyDoesNotStartAfterItsIncomingPeerStops(string reason)
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        PeerStore store = new();
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        await using PubsubRouter router = new(store, new PubsubSettings { ReconnectionPeriod = int.MaxValue });
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        TaskCompletionSource negotiation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(negotiation.Task);
        int replies = 0;
        try
        {
            await router.StartAsync(CreateLocal(session));
            store.Discover([address]);
            Task initialWork = PendingWork(router);
            (CancellationToken lifetime, Action suppress) = router.InboundConnection(address,
                PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                () => { Interlocked.Increment(ref replies); return Task.CompletedTask; });
            using CancellationTokenRegistration registration = lifetime.Register(() => closed.TrySetResult());
            if (reason == "closed")
            {
                incoming.SetResult();
                await closed.Task.WaitAsync(token);
            }
            else if (reason == "suppressed") suppress();
            else router.Dispose();
            negotiation.SetException(new IOException("Negotiation failed"));
            await initialWork.WaitAsync(token);
            Assert.That(Volatile.Read(ref replies), Is.Zero);
        }
        finally
        {
            negotiation.TrySetResult();
            incoming.TrySetResult();
        }
    }

    [TestCase(0, false)]
    [TestCase(2, false)]
    [TestCase(1, true)]
    public async Task FailedDeferredReplyKeepsTheRemainingRetryBudget(int retries, bool exhaustRetries)
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        PeerStore store = new();
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            ReconnectionAttempts = retries,
            ReconnectionPeriod = int.MaxValue,
            HeartbeatInterval = int.MaxValue,
        });
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        ILocalPeer local = CreateLocal(session);
        int physicalDials = 0;
        int initialDials = exhaustRetries ? 2 : 1;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Interlocked.Increment(ref physicalDials) == initialDials
                ? Task.FromResult(session)
                : Task.FromException<ISession>(new IOException("Connection failed")));
        TaskCompletionSource negotiation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(negotiation.Task);
        int replies = 0;
        try
        {
            await router.StartAsync(local);
            store.Discover([address]);
            if (exhaustRetries) router.Reconnect(CancellationToken.None);
            Task initialWork = PendingWork(router);
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task, () =>
            {
                Interlocked.Increment(ref replies);
                return Task.FromException(new IOException("Reply failed"));
            });
            negotiation.SetException(new IOException("Negotiation failed"));
            await initialWork.WaitAsync(token);
            for (int retry = 0; retry < retries + 3; retry++) router.Reconnect(CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(Volatile.Read(ref replies), Is.EqualTo(1));
                Assert.That(Volatile.Read(ref physicalDials), Is.EqualTo(initialDials + (exhaustRetries ? 0 : retries)));
            });
        }
        finally
        {
            negotiation.TrySetResult();
            incoming.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DiscoveryAndIncomingStreamOpenOnlyOneOutgoingStream(bool incomingFirst)
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store);
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        ILocalPeer local = CreateLocal(session);
        await router.StartAsync(local);
        TaskCompletionSource outgoing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int opened = 0;
        Task Reply()
        {
            Interlocked.Increment(ref opened);
            return outgoing.Task;
        }
        void AcceptIncoming() => router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12,
            incoming.Task, Reply);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref opened);
            AcceptIncoming();
            return outgoing.Task;
        });
        try
        {
            if (incomingFirst) AcceptIncoming();
            store.Discover([address]);
            Assert.That(Volatile.Read(ref opened), Is.EqualTo(1));
        }
        finally
        {
            outgoing.TrySetResult();
            incoming.TrySetResult();
        }
    }

    [Test]
    public async Task FailedReplyCanRetryWhileTheIncomingStreamRemainsOpen()
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store, new PubsubSettings { ReconnectionPeriod = int.MaxValue });
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        ILocalPeer local = CreateLocal(session);
        await router.StartAsync(local);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outgoing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int opened = 0;
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref opened);
            return outgoing.Task;
        });
        try
        {
            CancellationToken closed = router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12,
                incoming.Task, () => Task.FromException(new IOException("Negotiation failed"))).Token;
            router.Reconnect(CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(closed.IsCancellationRequested, Is.False);
                Assert.That(Volatile.Read(ref opened), Is.EqualTo(1));
            });
        }
        finally
        {
            outgoing.TrySetResult();
            incoming.TrySetResult();
        }
    }

    [Test]
    public async Task FailedReplyRetriesAfterItsIncomingGenerationWasRemoved()
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        PeerStore store = new();
        await using PubsubRouter router = new(store, new PubsubSettings { ReconnectionPeriod = int.MaxValue });
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        await router.StartAsync(CreateLocal(session));
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource outgoing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int opened = 0;
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref opened);
            return outgoing.Task;
        });
        try
        {
            CancellationToken closed = router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12,
                incoming.Task, () => reply.Task).Token;
            using CancellationTokenRegistration registration = closed.Register(() => removed.TrySetResult());
            incoming.SetResult();
            await removed.Task.WaitAsync(token);
            router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref opened), Is.Zero);

            reply.SetException(new IOException("Negotiation failed"));
            while (Volatile.Read(ref opened) == 0)
            {
                router.Reconnect(CancellationToken.None);
                await Task.Delay(10, token);
            }
            Assert.That(Volatile.Read(ref opened), Is.EqualTo(1));
        }
        finally
        {
            reply.TrySetResult();
            incoming.TrySetResult();
            outgoing.TrySetResult();
        }
    }

    [TestCase(false, 0)]
    [TestCase(false, 2)]
    [TestCase(true, 0)]
    [TestCase(true, 2)]
    public async Task FailedReplyHonorsAutoConnectAndRetryLimit(bool autoConnect, int retries)
    {
        PeerStore store = new();
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            AutoConnect = autoConnect,
            ReconnectionAttempts = retries,
            ReconnectionPeriod = int.MaxValue,
        });
        ISession session = Substitute.For<ISession>();
        ILocalPeer local = CreateLocal(session);
        int attempts = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException<ISession>(new IOException("Connection failed"));
        });
        await router.StartAsync(local);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            router.InboundConnection(TestPeers.Multiaddr(2), PubsubRouter.GossipsubProtocolVersionV12,
                incoming.Task, () => Task.FromException(new IOException("Negotiation failed")));
            for (int retry = 0; retry < retries + 3; retry++) router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref attempts), Is.EqualTo(autoConnect ? retries : 0));
        }
        finally { incoming.TrySetResult(); }
    }
    [Test]
    public async Task CompletedOldReplyCannotReleaseItsReplacementsReservation()
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false });
        await router.StartAsync(CreateLocal(Substitute.For<ISession>()));
        Multiaddress address = TestPeers.Multiaddr(2);
        TaskCompletionSource firstReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource thirdIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstWriter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int replies = 0;
        try
        {
            CancellationToken firstClosed = router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12,
                firstIncoming.Task, () => { Interlocked.Increment(ref replies); return firstReply.Task; }).Token;
            ConcurrentDictionary<Task, byte> work = (ConcurrentDictionary<Task, byte>)typeof(PubsubRouter)
                .GetField("_connects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
            Task firstWork = Task.WhenAll(work.Keys);
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, firstWriter.Task, _ => { });
            using CancellationTokenRegistration registration = firstClosed.Register(() => removed.TrySetResult());
            firstWriter.SetResult();
            await removed.Task.WaitAsync(token);

            CancellationToken secondClosed = router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12,
                secondIncoming.Task, () => { Interlocked.Increment(ref replies); return secondReply.Task; }).Token;
            Assert.That(Volatile.Read(ref replies), Is.EqualTo(2));
            firstReply.SetResult();
            await firstWork.WaitAsync(token);
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, thirdIncoming.Task,
                () => { Interlocked.Increment(ref replies); return Task.CompletedTask; });

            Assert.Multiple(() =>
            {
                Assert.That(Volatile.Read(ref replies), Is.EqualTo(2));
                Assert.That(secondClosed.IsCancellationRequested, Is.False);
            });
        }
        finally
        {
            firstReply.TrySetResult();
            secondReply.TrySetResult();
            firstIncoming.TrySetResult();
            secondIncoming.TrySetResult();
            thirdIncoming.TrySetResult();
            firstWriter.TrySetResult();
        }
    }

    private static ILocalPeer CreateLocal(ISession session)
    {
        ILocalPeer local = Substitute.For<ILocalPeer>();
        local.Identity.Returns(TestPeers.Identity(1));
        local.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(session);
        return local;
    }

    private static Task PendingWork(PubsubRouter router)
    {
        ConcurrentDictionary<Task, byte> work = (ConcurrentDictionary<Task, byte>)typeof(PubsubRouter)
            .GetField("_connects", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(router)!;
        return Task.WhenAll(work.Keys);
    }

}
