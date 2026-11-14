// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(5_000)]
public class OutboundConnectionRaceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task AnotherIncomingSessionGetsItsDeferredReplyWithoutARetryBudget(bool autoConnect)
    {
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            AutoConnect = autoConnect,
            ReconnectionAttempts = 0,
            ReconnectionPeriod = int.MaxValue,
        });
        await router.StartAsync(CreateLocal(Substitute.For<ISession>()));
        Multiaddress address = TestPeers.Multiaddr(2);
        TaskCompletionSource firstIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, firstIncoming.Task, () => firstReply.Task);
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, secondIncoming.Task,
                () => { secondReply.TrySetResult(); return Task.CompletedTask; });
            Assert.That(secondReply.Task.IsCompleted, Is.False);
            firstIncoming.SetResult();
            firstReply.SetException(new IOException("First session failed"));
            await secondReply.Task.WaitAsync(TestContext.CurrentContext.CancellationToken);
        }
        finally
        {
            firstIncoming.TrySetResult();
            firstReply.TrySetResult();
            secondIncoming.TrySetResult();
        }
    }

    [Test]
    public async Task WriterAttachmentDiscardsQueuedRecoveryAndResetsItsRetryBudget()
    {
        const int retries = 2;
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            ReconnectionAttempts = retries,
            ReconnectionPeriod = int.MaxValue,
        });
        ILocalPeer local = CreateLocal(Substitute.For<ISession>());
        int dials = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref dials);
            return Task.FromException<ISession>(new IOException("Connection failed"));
        });
        await router.StartAsync(local);
        Multiaddress address = TestPeers.Multiaddr(2);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CancellationToken closed = router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                () => Task.FromException(new IOException("Reply failed"))).Token;
            using CancellationTokenRegistration registration = closed.Register(() => removed.TrySetResult());
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, writer.Task, _ => { });
            router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref dials), Is.Zero);

            writer.SetResult();
            await removed.Task.WaitAsync(TestContext.CurrentContext.CancellationToken);
            for (int tick = 0; tick < retries + 2; tick++) router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref dials), Is.EqualTo(retries));
        }
        finally
        {
            incoming.TrySetResult();
            writer.TrySetResult();
        }
    }

    [Test]
    public async Task DirectLoopRepairsAnIncomingGenerationWithoutAWriter()
    {
        PeerStore store = new();
        Multiaddress address = TestPeers.Multiaddr(2);
        store.GetPeerInfo(address.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            DirectPeers = [address],
            ReconnectionPeriod = int.MaxValue,
            DirectConnectPeriod = int.MaxValue,
            HeartbeatInterval = int.MaxValue,
        });
        router.GetTopic("direct");
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(address);
        ILocalPeer local = CreateLocal(session);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Rpc> sent = [];
        int negotiations = 0;
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref negotiations) == 1) return Task.FromException(new IOException("Negotiation failed"));
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, writer.Task, sent.Add);
            return writer.Task;
        });
        try
        {
            await router.StartAsync(local);
            await router.PendingConnections.WaitAsync(TestContext.CurrentContext.CancellationToken);
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                () => Task.FromException(new IOException("Reply failed")));
            router.Reconnect(CancellationToken.None);
            _ = local.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());

            router.ConnectDirectPeers(CancellationToken.None);
            await router.FlushOutboundAsync();
            _ = local.Received(2).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
            Assert.That(sent.SelectMany(rpc => rpc.Subscriptions).Any(subscription => subscription.Topicid == "direct" && subscription.Subscribe), Is.True);
        }
        finally
        {
            incoming.TrySetResult();
            writer.TrySetResult();
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task ReconnectKeepsTheDiscoveredAddresses(bool deferredReply, bool multipleAddresses)
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        Multiaddress discovered = multipleAddresses
            ? $"/dns4/example.org/tcp/4001/p2p/{TestPeers.PeerId(2)}"
            : $"/ip4/10.0.0.2/tcp/4001/p2p/{TestPeers.PeerId(2)}";
        Multiaddress alternate = $"/ip4/10.0.0.3/tcp/4001/p2p/{TestPeers.PeerId(2)}";
        Multiaddress connected = $"/ip4/10.0.0.2/tcp/54321/p2p/{TestPeers.PeerId(2)}";
        PeerStore store = new();
        store.GetPeerInfo(TestPeers.PeerId(2)).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        await using PubsubRouter router = new(store, new PubsubSettings { ReconnectionPeriod = int.MaxValue, HeartbeatInterval = int.MaxValue });
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(connected);
        ILocalPeer local = CreateLocal(session);
        bool down = false;
        ConcurrentQueue<string> redialed = new();
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (!down) return Task.FromResult(session);
            foreach (Multiaddress address in call.Arg<Multiaddress[]>()) redialed.Enqueue(address.ToString());
            return Task.FromException<ISession>(new IOException("Peer is down"));
        });
        TaskCompletionSource dial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource writer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (!deferredReply) router.OutboundConnection(connected, PubsubRouter.GossipsubProtocolVersionV12, writer.Task, _ => { });
            return dial.Task;
        });
        try
        {
            await router.StartAsync(local);
            store.Discover(multipleAddresses ? [discovered, alternate] : [discovered]);
            Task work = router.PendingConnections;
            if (deferredReply)
                router.InboundConnection(connected, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                    () => Task.FromException(new OperationCanceledException()));

            down = true;
            writer.TrySetResult();
            dial.SetCanceled();
            await work.WaitAsync(token);
            router.Reconnect(CancellationToken.None);

            Assert.That(redialed, Does.Contain(discovered.ToString()));
            if (multipleAddresses) Assert.That(redialed, Does.Contain(alternate.ToString()));
        }
        finally
        {
            dial.TrySetResult();
            writer.TrySetResult();
            incoming.TrySetResult();
        }
    }

    [Test]
    public async Task NegotiationEndingWithoutWriterStartsItsDeferredReply()
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
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(negotiation.Task);
        int replies = 0;
        try
        {
            await router.StartAsync(CreateLocal(session));
            store.Discover([address]);
            Task work = router.PendingConnections;
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                () => { Interlocked.Increment(ref replies); return Task.CompletedTask; });

            negotiation.SetResult();
            await work.WaitAsync(token);

            Assert.That(Volatile.Read(ref replies), Is.EqualTo(1));
        }
        finally
        {
            negotiation.TrySetResult();
            incoming.TrySetResult();
        }
    }

    [Test]
    public async Task SessionLossDuringReplyNegotiationSpendsOneRetryBudget()
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        const int retries = 2;
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { ReconnectionAttempts = retries, ReconnectionPeriod = int.MaxValue });
        ILocalPeer local = CreateLocal(Substitute.For<ISession>());
        int dials = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref dials);
            return Task.FromException<ISession>(new IOException("Connection failed"));
        });
        await router.StartAsync(local);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CancellationToken closed = router.InboundConnection(TestPeers.Multiaddr(2), PubsubRouter.GossipsubProtocolVersionV12,
                incoming.Task, () => reply.Task).Token;
            using CancellationTokenRegistration registration = closed.Register(() => removed.TrySetResult());
            Task work = router.PendingConnections;
            incoming.SetResult();
            await removed.Task.WaitAsync(token);
            reply.SetCanceled();
            await work.WaitAsync(token);

            for (int tick = 0; tick < 2 * retries + 3; tick++) router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref dials), Is.EqualTo(retries));
        }
        finally
        {
            incoming.TrySetResult();
            reply.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedRepliesOnOneGenerationShareOneRetryBudget(bool directPeer)
    {
        const int retries = 2, streams = 3;
        Multiaddress address = TestPeers.Multiaddr(2);
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            DirectPeers = directPeer ? [address] : [],
            ReconnectionAttempts = retries,
            ReconnectionPeriod = int.MaxValue,
            DirectConnectPeriod = int.MaxValue,
        });
        ILocalPeer local = CreateLocal(Substitute.For<ISession>());
        int dials = 0;
        local.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref dials);
            return Task.FromException<ISession>(new IOException("Connection failed"));
        });
        await router.StartAsync(local);
        int directLoopDials = Volatile.Read(ref dials);
        TaskCompletionSource incoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            for (int stream = 0; stream < streams; stream++)
                router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                    () => Task.FromException(new IOException("Reply failed")));
            for (int tick = 0; tick < streams * retries + 3; tick++) router.Reconnect(CancellationToken.None);

            Assert.That(Volatile.Read(ref dials) - directLoopDials, Is.LessThanOrEqualTo(directPeer ? 0 : retries));
            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, incoming.Task,
                () => Task.FromException(new IOException("Reply still failed")));
            for (int tick = 0; tick < retries + 1; tick++) router.Reconnect(CancellationToken.None);
            Assert.That(Volatile.Read(ref dials) - directLoopDials, Is.LessThanOrEqualTo(directPeer ? 0 : retries));
        }
        finally { incoming.TrySetResult(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReplacementGenerationGetsAReplyWhenTheOlderReplyFails(bool autoConnect)
    {
        CancellationToken token = TestContext.CurrentContext.CancellationToken;
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = autoConnect, ReconnectionPeriod = int.MaxValue });
        await router.StartAsync(CreateLocal(Substitute.For<ISession>()));
        Multiaddress address = TestPeers.Multiaddr(2);
        TaskCompletionSource firstIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondIncoming = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int secondReplies = 0;
        try
        {
            CancellationToken firstClosed = router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12,
                firstIncoming.Task, () => firstReply.Task).Token;
            Task firstWork = router.PendingConnections;
            using CancellationTokenRegistration registration = firstClosed.Register(() => removed.TrySetResult());
            firstIncoming.SetResult();
            await removed.Task.WaitAsync(token);

            router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV12, secondIncoming.Task,
                () => { Interlocked.Increment(ref secondReplies); return Task.CompletedTask; });
            firstReply.SetException(new IOException("Negotiation failed"));
            await firstWork.WaitAsync(token);

            Assert.That(Volatile.Read(ref secondReplies), Is.EqualTo(1));
        }
        finally
        {
            firstIncoming.TrySetResult();
            firstReply.TrySetResult();
            secondIncoming.TrySetResult();
        }
    }

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
            Task initialWork = router.PendingConnections;
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
            _ = local.Received(exhaustRetries ? 2 : 1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
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
            Task initialWork = router.PendingConnections;
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
            Task initialWork = router.PendingConnections;
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

            Task pending = router.PendingConnections;
            reply.SetException(new IOException("Negotiation failed"));
            await pending.WaitAsync(token);
            router.Reconnect(CancellationToken.None);
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
            Task firstWork = router.PendingConnections;
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
}
