// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class TopicLifecycleTests
{
    [Test]
    public void Topic_UnsubscribeStopsDeliveryUntilResubscribed()
    {
        const string topicName = "topic-lifecycle";
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        ITopic topic = router.GetTopic(topicName);
        PeerId receivedFrom = TestPeers.PeerId(1);
        Identity author = TestPeers.Identity(2);
        int deliveries = 0;
        topic.OnMessage += (_, _) => deliveries++;

        topic.Unsubscribe();
        router.OnRpc(receivedFrom, CreateMessage(topicName, author, 1));

        Assert.Multiple(() =>
        {
            Assert.That(topic.IsSubscribed, Is.False);
            Assert.That(deliveries, Is.Zero);
        });

        topic.Subscribe();
        router.OnRpc(receivedFrom, CreateMessage(topicName, author, 2));

        Assert.Multiple(() =>
        {
            Assert.That(topic.IsSubscribed, Is.True);
            Assert.That(deliveries, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Topic_ResubscribeRetainsRemoteMembership()
    {
        const string topicName = "topic-lifecycle";
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        IRoutingStateContainer state = router;
        ITopic topic = router.GetTopic(topicName);
        Multiaddress peerAddress = TestPeers.Multiaddr(3);
        PeerId peerId = peerAddress.GetPeerId()!;
        TaskCompletionSource connectionClosed = new();
        List<Rpc> sent = [];

        router.OutboundConnection(peerAddress, PubsubRouter.GossipsubProtocolVersionV11, connectionClosed.Task, sent.Add);
        router.OnRpc(peerId, new Rpc().WithTopics([topicName], []));
        router.OnRpc(peerId, CreateMessage(topicName, TestPeers.Identity(2), 1));
        sent.Clear();

        topic.Unsubscribe();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[topicName], Has.Member(peerId));
            Assert.That(state.Mesh, Does.Not.ContainKey(topicName));
        });

        sent.Clear();
        await state.Heartbeat();
        Assert.That(sent, Is.Empty);

        topic.Subscribe();
        await state.Heartbeat();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[topicName], Has.Member(peerId));
            Assert.That(state.Mesh[topicName], Has.Member(peerId));
            Assert.That(sent.Any(rpc => rpc.Subscriptions.Any(subscription => subscription.Subscribe && subscription.Topicid == topicName)), Is.True);
        });

        connectionClosed.SetResult();
    }

    [Test]
    public void Topic_UnsubscribeNotifiesAllConnectedPeersAndDoesNotAnnounceItToNewPeers()
    {
        const string topicName = "topic-lifecycle";
        PubsubRouter router = new(new PeerStore());
        ITopic topic = router.GetTopic(topicName);
        TaskCompletionSource firstConnectionClosed = new();
        TaskCompletionSource secondConnectionClosed = new();
        TaskCompletionSource thirdConnectionClosed = new();
        List<Rpc> firstSent = [];
        List<Rpc> secondSent = [];
        List<Rpc> thirdSent = [];
        Multiaddress firstPeerAddress = TestPeers.Multiaddr(3);

        router.OutboundConnection(firstPeerAddress, PubsubRouter.GossipsubProtocolVersionV11, firstConnectionClosed.Task, firstSent.Add);
        router.OutboundConnection(TestPeers.Multiaddr(4), PubsubRouter.GossipsubProtocolVersionV11, secondConnectionClosed.Task, secondSent.Add);
        router.OnRpc(firstPeerAddress.GetPeerId()!, new Rpc().WithTopics([topicName], []));
        firstSent.Clear();
        secondSent.Clear();

        topic.Unsubscribe();

        router.OutboundConnection(TestPeers.Multiaddr(5), PubsubRouter.GossipsubProtocolVersionV11, thirdConnectionClosed.Task, thirdSent.Add);

        Assert.Multiple(() =>
        {
            Assert.That(firstSent.Any(rpc => rpc.Subscriptions.Any(subscription => !subscription.Subscribe && subscription.Topicid == topicName)), Is.True);
            Assert.That(secondSent.Any(rpc => rpc.Subscriptions.Any(subscription => !subscription.Subscribe && subscription.Topicid == topicName)), Is.True);
            Assert.That(thirdSent.Any(rpc => rpc.Subscriptions.Any(subscription => subscription.Subscribe && subscription.Topicid == topicName)), Is.False);
        });

        firstConnectionClosed.SetResult();
        secondConnectionClosed.SetResult();
        thirdConnectionClosed.SetResult();
    }

    [Test]
    public async Task PublishOnlyTopic_ContinuesGossiping()
    {
        const string topicName = "topic-lifecycle";
        PubsubSettings settings = new() { Degree = 1, LazyDegree = 1, GossipFactor = 1 };
        PubsubRouter router = new(new PeerStore(), settings);
        IRoutingStateContainer state = router;
        _ = router.GetTopic(topicName, subscribe: false);
        Multiaddress fanoutPeerAddress = TestPeers.Multiaddr(3);
        Multiaddress gossipPeerAddress = TestPeers.Multiaddr(4);
        PeerId fanoutPeerId = fanoutPeerAddress.GetPeerId()!;
        PeerId gossipPeerId = gossipPeerAddress.GetPeerId()!;
        TaskCompletionSource fanoutConnectionClosed = new();
        TaskCompletionSource gossipConnectionClosed = new();
        List<Rpc> fanoutSent = [];
        List<Rpc> gossipSent = [];

        router.OutboundConnection(fanoutPeerAddress, PubsubRouter.GossipsubProtocolVersionV11, fanoutConnectionClosed.Task, fanoutSent.Add);
        router.OutboundConnection(gossipPeerAddress, PubsubRouter.GossipsubProtocolVersionV11, gossipConnectionClosed.Task, gossipSent.Add);
        router.OnRpc(fanoutPeerId, new Rpc().WithTopics([topicName], []));
        router.OnRpc(gossipPeerId, new Rpc().WithTopics([topicName], []));
        state.Fanout.GetOrAdd(topicName, []).Add(fanoutPeerId);
        router.OnRpc(fanoutPeerId, CreateMessage(topicName, TestPeers.Identity(5), 1));
        fanoutSent.Clear();
        gossipSent.Clear();

        await state.Heartbeat();

        Assert.That(gossipSent.Any(rpc => rpc.Control?.Ihave.Any(ihave => ihave.TopicID == topicName) is true), Is.True);

        fanoutConnectionClosed.SetResult();
        gossipConnectionClosed.SetResult();
    }

    [Test]
    public void UnsubscribedTopic_DoesNotRequestAdvertisedMessages()
    {
        const string topicName = "topic-lifecycle";
        PubsubRouter router = new(new PeerStore());
        ITopic topic = router.GetTopic(topicName);
        Multiaddress peerAddress = TestPeers.Multiaddr(3);
        TaskCompletionSource connectionClosed = new();
        List<Rpc> sent = [];

        router.OutboundConnection(peerAddress, PubsubRouter.GossipsubProtocolVersionV11, connectionClosed.Task, sent.Add);
        topic.Unsubscribe();
        sent.Clear();

        Rpc rpc = new() { Control = new ControlMessage() };
        rpc.Control.Ihave.Add(new ControlIHave { TopicID = topicName, MessageIDs = { ByteString.CopyFrom([1]) } });
        router.OnRpc(peerAddress.GetPeerId()!, rpc);

        Assert.That(sent, Is.Empty);

        connectionClosed.SetResult();
    }

    [Test]
    public void Topic_SubscribeMovesFanoutPeersIntoTheMesh()
    {
        const string topicName = "topic-lifecycle";
        PubsubRouter router = new(new PeerStore());
        IRoutingStateContainer state = router;
        ITopic topic = router.GetTopic(topicName, subscribe: false);
        PeerId fanoutPeer = TestPeers.PeerId(3);
        state.Fanout.GetOrAdd(topicName, []).Add(fanoutPeer);

        topic.Subscribe();

        Assert.Multiple(() =>
        {
            Assert.That(state.Mesh[topicName], Has.Member(fanoutPeer));
            Assert.That(state.Fanout, Does.Not.ContainKey(topicName));
        });
    }

    private static Rpc CreateMessage(string topicName, Identity author, ulong sequenceNumber)
    {
        return new Rpc().WithMessages(topicName, sequenceNumber, author.PeerId.Bytes, [1, 2, 3], author);
    }
}
