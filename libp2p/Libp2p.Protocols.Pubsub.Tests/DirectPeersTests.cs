// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class DirectPeersTests
{
    [Test]
    public void DirectPeers_ForwardValidMessagesDespitePeerScores()
    {
        const string topic = "topic";
        Multiaddress senderAddress = TestPeers.Multiaddr(1);
        Multiaddress receiverAddress = TestPeers.Multiaddr(2);
        PeerId senderPeerId = senderAddress.GetPeerId()!;
        PeerId receiverPeerId = receiverAddress.GetPeerId()!;
        PubsubRouter router = new(
            new PeerStore(),
            new PubsubSettings { DirectPeers = [senderAddress, receiverAddress] });
        ITopic localTopic = router.GetTopic(topic);
        List<Rpc> senderRpcs = [];
        List<Rpc> receiverRpcs = [];
        TaskCompletionSource senderConnection = new();
        TaskCompletionSource receiverConnection = new();
        router.OutboundConnection(senderAddress, PubsubRouter.GossipsubProtocolVersionV11, senderConnection.Task, senderRpcs.Add);
        router.OutboundConnection(receiverAddress, PubsubRouter.GossipsubProtocolVersionV11, receiverConnection.Task, receiverRpcs.Add);
        router.OnRpc(senderPeerId, new Rpc().WithTopics([topic], []));
        router.OnRpc(receiverPeerId, new Rpc().WithTopics([topic], []));

        router.SetAppSpecificScore(senderPeerId, -20);
        router.SetAppSpecificScore(receiverPeerId, -20);
        senderRpcs.Clear();
        receiverRpcs.Clear();

        PeerId? receivedFrom = null;
        localTopic.OnMessage += (peerId, _) => receivedFrom = peerId;
        Identity author = TestPeers.Identity(1);
        router.OnRpc(senderPeerId, new Rpc().WithMessages(topic, 1, author.PeerId.Bytes, [1, 2, 3], author));

        Assert.Multiple(() =>
        {
            Assert.That(receivedFrom, Is.EqualTo(senderPeerId));
            Assert.That(receiverRpcs.Single().Publish.Single().Data.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(senderRpcs, Is.Empty);
        });

        senderConnection.SetResult();
        receiverConnection.SetResult();
    }

    [Test]
    public void DirectPeers_AreNeverAddedToTheMesh()
    {
        const string topic = "topic";
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        Multiaddress firstMeshAddress = TestPeers.Multiaddr(2);
        Multiaddress secondMeshAddress = TestPeers.Multiaddr(3);
        PubsubRouter router = new(
            new PeerStore(),
            new PubsubSettings { DirectPeers = [directAddress] });
        IRoutingStateContainer state = router;
        _ = router.GetTopic(topic);
        TaskCompletionSource connection = new();

        foreach (Multiaddress address in new[] { directAddress, firstMeshAddress, secondMeshAddress })
        {
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV11, connection.Task, _ => { });
            router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics([topic], []));
        }

        router.Heartbeat().GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[topic], Has.Member(directAddress.GetPeerId()));
            Assert.That(state.Mesh[topic], Does.Not.Contain(directAddress.GetPeerId()));
            Assert.That(state.Mesh[topic], Has.Count.EqualTo(2));
        });

        connection.SetResult();
    }

    [Test]
    public void DirectPeerGrafts_AreRejectedWithPrune()
    {
        const string topic = "topic";
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        PeerId directPeerId = directAddress.GetPeerId()!;
        PubsubRouter router = new(
            new PeerStore(),
            new PubsubSettings { DirectPeers = [directAddress], PruneBackoff = 2_000 });
        _ = router.GetTopic(topic);
        List<Rpc> sentRpcs = [];
        TaskCompletionSource connection = new();
        router.OutboundConnection(directAddress, PubsubRouter.GossipsubProtocolVersionV11, connection.Task, sentRpcs.Add);
        sentRpcs.Clear();

        Rpc graft = new() { Control = new ControlMessage() };
        graft.Control.Graft.Add(new ControlGraft { TopicID = topic });
        router.OnRpc(directPeerId, graft);

        ControlPrune prune = sentRpcs.Single().Control.Prune.Single();
        Assert.Multiple(() =>
        {
            Assert.That(prune.TopicID, Is.EqualTo(topic));
            Assert.That(prune.Backoff, Is.EqualTo(2));
        });
        connection.SetResult();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Router_ConnectsConfiguredDirectPeersAtStartup(bool addressesAlreadyKnown)
    {
        PeerStore peerStore = new();
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        PeerId directPeerId = directAddress.GetPeerId()!;
        peerStore.GetPeerInfo(directPeerId).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        if (addressesAlreadyKnown)
        {
            peerStore.Discover([directAddress]);
        }
        PubsubRouter router = new(peerStore, new PubsubSettings { DirectPeers = [directAddress] });

        TaskCompletionSource protocolDialed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(directAddress);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            protocolDialed.TrySetResult();
            return Task.CompletedTask;
        });

        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(session);

        using CancellationTokenSource cancellation = new();
        await router.StartAsync(localPeer, cancellation.Token);

        await protocolDialed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = localPeer.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        _ = session.Received(1).DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>());

        cancellation.Cancel();
    }

    [Test]
    public async Task PublishPartial_ExcludesDirectPeersFromFanoutAndMesh()
    {
        const string topic = "topic";
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        Multiaddress otherAddress = TestPeers.Multiaddr(2);
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            DirectPeers = [directAddress],
            EnablePartialMessages = true,
        });
        IRoutingStateContainer state = router;
        router.GetPartialMessagesTopic(topic,
            new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: false);
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(3));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource connection = new();
        try
        {
            await router.StartAsync(localPeer, cancellation.Token);
            foreach (Multiaddress address in new[] { directAddress, otherAddress })
            {
                router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });
                router.OnRpc(address.GetPeerId()!, new Rpc().WithTopics([topic], []));
            }

            router.PublishPartial(topic, [1], partialMessage: [2]);
            Assert.That(state.Fanout[topic], Is.EquivalentTo(new[] { otherAddress.GetPeerId() }));

            router.Subscribe(topic);
            Assert.That(state.Mesh[topic], Is.EquivalentTo(new[] { otherAddress.GetPeerId() }));
        }
        finally
        {
            cancellation.Cancel();
            connection.TrySetResult();
        }
    }

    [Test]
    public void Subscribe_ExcludesDirectPeersWhenMovingFanoutToMesh()
    {
        const string topic = "topic";
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        Multiaddress otherAddress = TestPeers.Multiaddr(2);
        PeerId otherPeerId = otherAddress.GetPeerId()!;
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { DirectPeers = [directAddress] });
        IRoutingStateContainer state = router;
        TaskCompletionSource connection = new();
        try
        {
            router.OutboundConnection(otherAddress, PubsubRouter.GossipsubProtocolVersionV12, connection.Task, _ => { });
            router.OnRpc(otherPeerId, new Rpc().WithTopics([topic], []));
            state.Fanout[topic] = [directAddress.GetPeerId()!, otherPeerId];

            router.Subscribe(topic);

            Assert.That(state.Mesh[topic], Is.EquivalentTo(new[] { otherPeerId }));
            Assert.That(state.Fanout.ContainsKey(topic), Is.False);
        }
        finally
        {
            connection.SetResult();
        }
    }

    [Test]
    public async Task Router_ReconnectsDisconnectedDirectPeersIndependentlyOfOtherIntervals()
    {
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        PeerStore peerStore = new();
        peerStore.GetPeerInfo(directAddress.GetPeerId()!).SupportedProtocols = [PubsubRouter.GossipsubProtocolVersionV12];
        using PubsubRouter router = new(peerStore, new PubsubSettings
        {
            DirectPeers = [directAddress],
            DirectConnectPeriod = 50,
            ReconnectionPeriod = 60_000,
            HeartbeatInterval = 60_000,
        });
        TaskCompletionSource firstConnection = new();
        TaskCompletionSource secondConnection = new();
        TaskCompletionSource redialed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int protocolDials = 0;
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(directAddress);
        session.DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            int attempt = Interlocked.Increment(ref protocolDials);
            router.OutboundConnection(directAddress, PubsubRouter.GossipsubProtocolVersionV12,
                attempt == 1 ? firstConnection.Task : secondConnection.Task, _ => { });
            if (attempt > 1)
            {
                redialed.TrySetResult();
            }
            return Task.CompletedTask;
        });
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(session);
        using CancellationTokenSource cancellation = new();
        try
        {
            await router.StartAsync(localPeer, cancellation.Token);
            await Task.Delay(200);
            _ = localPeer.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());

            firstConnection.SetResult();
            await redialed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await Task.Delay(200);
            _ = localPeer.Received(2).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
            _ = session.Received(2).DialAsync<GossipsubProtocolV12>(Arg.Any<CancellationToken>());
        }
        finally
        {
            cancellation.Cancel();
            firstConnection.TrySetResult();
            secondConnection.TrySetResult();
        }
    }

    [Test]
    public async Task Router_DoesNotRepeatInFlightDirectPeerDials()
    {
        Multiaddress directAddress = TestPeers.Multiaddr(1);
        PeerStore peerStore = new();
        peerStore.Discover([directAddress]);
        await using PubsubRouter router = new(peerStore, new PubsubSettings
        {
            DirectPeers = [directAddress],
            DirectConnectPeriod = 20,
            ReconnectionPeriod = 60_000,
            HeartbeatInterval = 60_000,
        });
        TaskCompletionSource<ISession> slowDial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(slowDial.Task);

        try
        {
            await router.StartAsync(localPeer);
            await Task.Delay(200);

            _ = localPeer.Received(1).DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            slowDial.TrySetCanceled();
        }
    }

    [Test]
    public void DirectPeers_RequirePeerIdsInTheirAddresses()
    {
        PubsubSettings settings = new()
        {
            DirectPeers = new[] { Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001") },
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new PubsubRouter(new PeerStore(), settings))!;
        Assert.That(exception.ParamName, Is.EqualTo(nameof(PubsubSettings.DirectPeers)));
    }
}
