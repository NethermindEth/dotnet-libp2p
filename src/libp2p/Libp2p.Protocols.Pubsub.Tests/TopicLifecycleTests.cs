// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
        sent.Clear();

        topic.Unsubscribe();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[topicName], Has.Member(peerId));
            Assert.That(state.Mesh, Does.Not.ContainKey(topicName));
        });

        sent.Clear();
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

    private static Rpc CreateMessage(string topicName, Identity author, ulong sequenceNumber)
    {
        return new Rpc().WithMessages(topicName, sequenceNumber, author.PeerId.Bytes, [1, 2, 3], author);
    }
}
