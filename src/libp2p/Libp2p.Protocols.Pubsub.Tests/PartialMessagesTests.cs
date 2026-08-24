// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PartialMessagesTests
{
    [Test]
    public void PartialMessagesTopics_RequireExplicitRouterAndTopicOptIn()
    {
        PubsubRouter disabledRouter = new(new PeerStore());
        Assert.That(
            () => disabledRouter.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions { SupportSendingPartialMessages = true }),
            Throws.TypeOf<InvalidOperationException>());

        PubsubRouter enabledRouter = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        Assert.That(
            () => enabledRouter.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions { RequestPartialMessages = true }),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task PartialMessages_AdvertiseCapabilitiesAndGatePayloadsPerPeer()
    {
        const string topicName = "topic";
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic(
            topicName,
            new PartialMessagesTopicOptions
            {
                RequestPartialMessages = true,
                SupportSendingPartialMessages = true,
            });
        await router.StartAsync(new LocalPeerStub());

        List<Rpc> requestedPeerRpcs = [];
        List<Rpc> metadataOnlyPeerRpcs = [];
        TaskCompletionSource firstConnection = new();
        TaskCompletionSource secondConnection = new();
        Multiaddress requestedPeerAddress = TestPeers.Multiaddr(1);
        Multiaddress metadataOnlyPeerAddress = TestPeers.Multiaddr(2);
        PeerId requestedPeerId = requestedPeerAddress.GetPeerId()!;
        PeerId metadataOnlyPeerId = metadataOnlyPeerAddress.GetPeerId()!;

        router.OutboundConnection(requestedPeerAddress, PubsubRouter.GossipsubProtocolVersionV13, firstConnection.Task, requestedPeerRpcs.Add);
        router.OutboundConnection(metadataOnlyPeerAddress, PubsubRouter.GossipsubProtocolVersionV13, secondConnection.Task, metadataOnlyPeerRpcs.Add);

        Assert.Multiple(() =>
        {
            Assert.That(requestedPeerRpcs.Single().Control.Extensions.PartialMessages, Is.True);
            Assert.That(requestedPeerRpcs.Single().Subscriptions.Single().RequestsPartial, Is.True);
            Assert.That(requestedPeerRpcs.Single().Subscriptions.Single().SupportsSendingPartial, Is.True);
            Assert.That(metadataOnlyPeerRpcs.Single().Control.Extensions.PartialMessages, Is.True);
        });

        router.OnRpc(requestedPeerId, CreateSubscriptionRpc(topicName, requestsPartialMessages: true, supportsSendingPartialMessages: true));
        router.OnRpc(metadataOnlyPeerId, CreateSubscriptionRpc(topicName, requestsPartialMessages: false, supportsSendingPartialMessages: true));
        requestedPeerRpcs.Clear();
        metadataOnlyPeerRpcs.Clear();

        topic.PublishPartial([1, 2], partialMessage: [3], partsMetadata: [4]);

        PartialMessagesExtension requestedPartial = requestedPeerRpcs.Single().Partial;
        PartialMessagesExtension metadataOnlyPartial = metadataOnlyPeerRpcs.Single().Partial;
        Assert.Multiple(() =>
        {
            Assert.That(requestedPartial.GroupID.ToByteArray(), Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(requestedPartial.PartialMessage.ToByteArray(), Is.EqualTo(new byte[] { 3 }));
            Assert.That(requestedPartial.PartsMetadata.ToByteArray(), Is.EqualTo(new byte[] { 4 }));
            Assert.That(metadataOnlyPartial.HasPartialMessage, Is.False);
            Assert.That(metadataOnlyPartial.PartsMetadata.ToByteArray(), Is.EqualTo(new byte[] { 4 }));
        });

        firstConnection.SetResult();
        secondConnection.SetResult();
    }

    [Test]
    public void PartialMessages_AreForwardedAfterThePeerAdvertisesTheExtension()
    {
        const string topicName = "topic";
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic(
            topicName,
            new PartialMessagesTopicOptions { SupportSendingPartialMessages = true });
        Multiaddress remoteAddress = TestPeers.Multiaddr(1);
        PeerId remotePeerId = remoteAddress.GetPeerId()!;
        TaskCompletionSource connection = new();
        router.OutboundConnection(remoteAddress, PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });

        PartialMessage? received = null;
        topic.OnPartialMessage += (_, message) => received = message;

        router.OnRpc(remotePeerId, new Rpc
        {
            Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } },
        });
        router.OnRpc(remotePeerId, new Rpc
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = topicName,
                GroupID = ByteString.CopyFrom([1]),
                PartialMessage = ByteString.CopyFrom([2]),
                PartsMetadata = ByteString.CopyFrom([3]),
            },
        });

        Assert.Multiple(() =>
        {
            Assert.That(received, Is.Not.Null);
            Assert.That(received!.GroupId, Is.EqualTo(new byte[] { 1 }));
            Assert.That(received.PartialData, Is.EqualTo(new byte[] { 2 }));
            Assert.That(received.PartsMetadata, Is.EqualTo(new byte[] { 3 }));
        });

        connection.SetResult();
    }

    [Test]
    public void PartialMessages_AreNeverAdvertisedOnOlderProtocols()
    {
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        _ = router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions { SupportSendingPartialMessages = true });
        List<Rpc> sentRpcs = [];
        TaskCompletionSource connection = new();

        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV12, connection.Task, sentRpcs.Add);

        Assert.That(sentRpcs.Single().Control, Is.Null);
        connection.SetResult();
    }

    private static Rpc CreateSubscriptionRpc(string topicName, bool requestsPartialMessages, bool supportsSendingPartialMessages)
    {
        Rpc rpc = new()
        {
            Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } },
        };
        rpc.Subscriptions.Add(new Rpc.Types.SubOpts
        {
            Subscribe = true,
            Topicid = topicName,
            RequestsPartial = requestsPartialMessages,
            SupportsSendingPartial = supportsSendingPartialMessages,
        });
        return rpc;
    }
}
