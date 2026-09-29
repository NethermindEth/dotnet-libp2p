// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class GossipsubControlLimitsTests
{
    [Test]
    public async Task Ihave_CountsRpcsRatherThanTopicEnvelopes()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIHaveMessages = 1,
            MaxIHaveLength = 20,
        });

        Rpc ihaves = CreateIhave("unsubscribed", [0]);
        for (byte i = 1; i <= 12; i++)
        {
            string topic = $"topic-{i}";
            _ = setup.Router.GetTopic(topic);
            ihaves.Control.Ihave.Add(new ControlIHave { TopicID = topic, MessageIDs = { ByteString.CopyFrom([i]) } });
        }
        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, ihaves);

        Assert.That(GetIwantIds(setup.SentRpcs), Is.EquivalentTo(
            Enumerable.Range(1, 12).Select(i => ByteString.CopyFrom([(byte)i]))));
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [4]));
        Assert.That(GetIwantIds(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [5]));
        Assert.That(GetIwantIds(setup.SentRpcs), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Ihave_BoundsRequestsAcrossRpcsAndResetsOnHeartbeat()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIHaveLength = 3,
        });

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [1], [2]));
        Assert.That(GetIwantIds(setup.SentRpcs), Has.Count.EqualTo(2));
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [3], [4], [5], [6]));
        Assert.That(GetIwantIds(setup.SentRpcs), Has.Count.EqualTo(1));
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [7]));
        Assert.That(GetIwantIds(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [8], [9], [10], [11]));
        Assert.That(GetIwantIds(setup.SentRpcs), Has.Count.EqualTo(3));
    }

    [Test]
    public async Task Ihave_BoundsInspectedIdsEvenWhenTheyAreDuplicates()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIHaveLength = 3,
        });

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [1], [1], [1], [2]));

        Assert.That(GetIwantIds(setup.SentRpcs), Is.EqualTo(new[] { ByteString.CopyFrom([1]) }));
    }

    [Test]
    public async Task Iwant_UsesRetransmissionAndResponseByteLimits()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            GossipRetransmission = 2,
            MaxIwantResponseBytes = 200,
        });

        MessageId first = setup.Publish([1, 2, 3]);
        MessageId second = setup.Publish(new byte[64]);

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first, second));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);
    }

    [Test]
    public async Task Iwant_ResponseLimitIncludesProtobufEnvelopeOverhead()
    {
        PubsubSettings settings = new() { HeartbeatInterval = int.MaxValue };
        await using RouterSetup setup = await RouterSetup.Create(settings);
        MessageId messageId = setup.Publish([1, 2, 3]);
        settings.MaxIwantResponseBytes = setup.SentRpcs.Last().Publish.Single().CalculateSize();
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));

        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);
    }

    [Test]
    public async Task IDontWant_SuppressesResponsesUntilItsHeartbeatTtlExpires()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            IdontwantTtlHeartbeats = 1,
        });
        MessageId messageId = setup.Publish([1, 2, 3]);
        setup.SentRpcs.Clear();

        Rpc unwanted = new() { Control = new ControlMessage() };
        unwanted.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(messageId.Bytes) } });
        setup.Router.OnRpc(setup.RemotePeerId, unwanted);
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IDontWant_SuppressesMeshForwarding(bool sendIdontwant)
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        PeerId senderPeerId = TestPeers.PeerId(3);
        TaskCompletionSource senderConnection = new();
        setup.Router.OutboundConnection(TestPeers.Multiaddr(3), PubsubRouter.GossipsubProtocolVersionV12, senderConnection.Task, _ => { });
        setup.Router.OnRpc(senderPeerId, new Rpc().WithTopics([setup.Topic], []));
        await setup.Router.Heartbeat();
        Assert.That(((IRoutingStateContainer)setup.Router).Mesh[setup.Topic], Is.SupersetOf(new[] { setup.RemotePeerId, senderPeerId }));

        Identity author = TestPeers.Identity(4);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, [1], author).Publish.Single();
        if (sendIdontwant)
        {
            Rpc unwanted = new() { Control = new ControlMessage() };
            unwanted.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(message).Bytes) } });
            setup.Router.OnRpc(setup.RemotePeerId, unwanted);
        }
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(senderPeerId, new Rpc { Publish = { message } });

        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(sendIdontwant ? 0 : 1));
        senderConnection.SetResult();
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task IDontWant_SuppressesLocalPublishToDirectMeshFloodAndFanoutPeers(bool floodPublish, bool useFanout)
    {
        PubsubSettings settings = new()
        {
            HeartbeatInterval = int.MaxValue,
            FloodPublish = floodPublish,
            DirectPeers = [TestPeers.Multiaddr(3)],
            GetMessageId = message => new MessageId(message.Data.ToByteArray()),
        };
        await using RouterSetup setup = await RouterSetup.Create(settings);
        List<Rpc> directRpcs = [];
        List<Rpc> allowedRpcs = [];
        List<Rpc> floodsubRpcs = [];
        PeerId directPeer = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, directRpcs);
        _ = setup.ConnectPeer(4, PubsubRouter.GossipsubProtocolVersionV12, allowedRpcs);
        _ = setup.ConnectPeer(5, PubsubRouter.FloodsubProtocolVersion, floodsubRpcs);
        await setup.Router.Heartbeat();
        if (useFanout)
        {
            setup.Router.Unsubscribe(setup.Topic);
        }

        MessageId messageId = new([42]);
        Rpc unwanted = new() { Control = new ControlMessage() };
        unwanted.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(messageId.Bytes) } });
        setup.Router.OnRpc(setup.RemotePeerId, unwanted);
        setup.Router.OnRpc(directPeer, unwanted);
        setup.SentRpcs.Clear();
        directRpcs.Clear();
        allowedRpcs.Clear();
        floodsubRpcs.Clear();

        setup.Router.Publish(setup.Topic, [42]);

        Assert.Multiple(() =>
        {
            Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);
            Assert.That(GetPublishedMessages(directRpcs), Is.Empty);
            Assert.That(GetPublishedMessages(allowedRpcs), Has.Count.EqualTo(1));
            Assert.That(GetPublishedMessages(floodsubRpcs), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task IDontWant_SendsFirstLargeMessageIdToV12MeshPeersBeforeForwarding()
    {
        PubsubSettings settings = new()
        {
            HeartbeatInterval = int.MaxValue,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()[..20]),
        };
        await using RouterSetup setup = await RouterSetup.Create(settings);
        List<Rpc> senderRpcs = [];
        List<Rpc> oldPeerRpcs = [];
        List<Rpc> v13PeerRpcs = [];
        PeerId sender = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, senderRpcs);
        _ = setup.ConnectPeer(4, PubsubRouter.GossipsubProtocolVersionV11, oldPeerRpcs);
        _ = setup.ConnectPeer(5, PubsubRouter.GossipsubProtocolVersionV13, v13PeerRpcs);
        await setup.Router.Heartbeat();
        Assert.That(((IRoutingStateContainer)setup.Router).Mesh[setup.Topic], Has.Count.EqualTo(4));
        setup.SentRpcs.Clear();
        senderRpcs.Clear();
        oldPeerRpcs.Clear();
        v13PeerRpcs.Clear();

        Identity author = TestPeers.Identity(6);
        byte[] data = new byte[1024];
        for (int i = 0; i < 20; i++)
        {
            data[i] = (byte)(i + 1);
        }
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, data, author).Publish.Single();
        ByteString expectedId = ByteString.CopyFrom(settings.GetMessageId(message).Bytes);

        setup.Router.OnRpc(sender, new Rpc { Publish = { message } });

        Assert.Multiple(() =>
        {
            Assert.That(GetIdontwantIds(setup.SentRpcs), Is.EqualTo(new[] { expectedId }));
            Assert.That(GetIdontwantIds(v13PeerRpcs), Is.EqualTo(new[] { expectedId }));
            Assert.That(GetIdontwantIds(senderRpcs), Is.Empty);
            Assert.That(GetIdontwantIds(oldPeerRpcs), Is.Empty);
            Assert.That(GetPublishedMessages(setup.SentRpcs), Is.EqualTo(new[] { message }));
            Assert.That(GetPublishedMessages(oldPeerRpcs), Is.EqualTo(new[] { message }));
            Assert.That(GetPublishedMessages(v13PeerRpcs), Is.EqualTo(new[] { message }));
            Assert.That(setup.SentRpcs.FindIndex(rpc => rpc.Control?.Idontwant.Count > 0),
                Is.LessThan(setup.SentRpcs.FindIndex(rpc => rpc.Publish.Count > 0)));
        });

        setup.Router.OnRpc(sender, new Rpc { Publish = { message } });
        Assert.That(GetIdontwantIds(setup.SentRpcs), Is.EqualTo(new[] { expectedId }));
    }

    [Test]
    public async Task IDontWant_AnnouncesLocallyPublishedLargeMessageBeforeSendingIt()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        List<Rpc> oldPeerRpcs = [];
        List<Rpc> v13PeerRpcs = [];
        _ = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV11, oldPeerRpcs);
        _ = setup.ConnectPeer(4, PubsubRouter.GossipsubProtocolVersionV13, v13PeerRpcs);
        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        oldPeerRpcs.Clear();
        v13PeerRpcs.Clear();

        setup.Router.Publish(setup.Topic, new byte[1024]);

        Message published = GetPublishedMessages(setup.SentRpcs).Single();
        ByteString expectedId = ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(published).Bytes);
        Assert.Multiple(() =>
        {
            Assert.That(GetIdontwantIds(setup.SentRpcs), Is.EqualTo(new[] { expectedId }));
            Assert.That(GetIdontwantIds(v13PeerRpcs), Is.EqualTo(new[] { expectedId }));
            Assert.That(GetIdontwantIds(oldPeerRpcs), Is.Empty);
            Assert.That(setup.SentRpcs.FindIndex(rpc => rpc.Control?.Idontwant.Count > 0),
                Is.LessThan(setup.SentRpcs.FindIndex(rpc => rpc.Publish.Count > 0)));
        });
    }

    [Test]
    public async Task IDontWant_UnknownVerdictCannotAnnounceOrForward()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        List<Rpc> recipientRpcs = [];
        _ = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, recipientRpcs);
        await setup.Router.Heartbeat();
        recipientRpcs.Clear();
        Identity author = TestPeers.Identity(4);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, new byte[1024], author).Publish.Single();
        setup.Router.VerifyMessage = _ => (MessageValidity)int.MaxValue;

        setup.Router.OnRpc(setup.RemotePeerId, new Rpc { Publish = { message } });

        Assert.Multiple(() =>
        {
            Assert.That(GetIdontwantIds(recipientRpcs), Is.Empty);
            Assert.That(GetPublishedMessages(recipientRpcs), Is.Empty);
        });
    }

    [Test]
    public async Task IDontWant_BoundsOutboundMessagesPerPeerPerHeartbeat()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIdontwantMessages = 1,
        });
        List<Rpc> senderRpcs = [];
        PeerId sender = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, senderRpcs);
        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        Identity author = TestPeers.Identity(4);

        Message first = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, new byte[1024], author).Publish.Single();
        Message second = new Rpc().WithMessages(setup.Topic, 2, author.PeerId.Bytes, new byte[1024], author).Publish.Single();
        Message third = new Rpc().WithMessages(setup.Topic, 3, author.PeerId.Bytes, new byte[1024], author).Publish.Single();
        setup.Router.OnRpc(sender, new Rpc { Publish = { first, second } });
        Rpc idontwant = setup.SentRpcs.Single(rpc => rpc.Control?.Idontwant.Count > 0);
        Assert.Multiple(() =>
        {
            Assert.That(idontwant.Control.Idontwant, Has.Count.EqualTo(1));
            Assert.That(GetIdontwantIds(setup.SentRpcs), Is.EqualTo(new[]
            {
                ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(first).Bytes),
                ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(second).Bytes),
            }));
        });

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(sender, new Rpc { Publish = { third } });
        Assert.That(GetIdontwantIds(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        Message fourth = new Rpc().WithMessages(setup.Topic, 4, author.PeerId.Bytes, new byte[1024], author).Publish.Single();
        setup.Router.OnRpc(sender, new Rpc { Publish = { fourth } });
        Assert.That(GetIdontwantIds(setup.SentRpcs), Is.EqualTo(new[] { ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(fourth).Bytes) }));
    }

    [Test]
    public async Task IDontWant_BoundsBatchLengthAndEnvelopeCount()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIdontwantMessages = 2,
            MaxIdontwantLength = 2,
        });
        List<Rpc> senderRpcs = [];
        PeerId sender = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, senderRpcs);
        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        Identity author = TestPeers.Identity(4);
        Message[] messages = Enumerable.Range(1, 5)
            .Select(sequence => new Rpc().WithMessages(setup.Topic, (ulong)sequence, author.PeerId.Bytes, new byte[1024], author).Publish.Single())
            .ToArray();

        setup.Router.OnRpc(sender, new Rpc { Publish = { messages } });

        Rpc idontwant = setup.SentRpcs.Single(rpc => rpc.Control?.Idontwant.Count > 0);
        Assert.Multiple(() =>
        {
            Assert.That(idontwant.Control.Idontwant.Select(envelope => envelope.MessageIDs.Count), Is.EqualTo(new[] { 2, 2 }));
            Assert.That(GetIdontwantIds(setup.SentRpcs), Is.EqualTo(messages.Take(4).Select(message => ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(message).Bytes))));
            Assert.That(GetPublishedMessages(setup.SentRpcs), Is.EqualTo(messages));
        });
    }

    [Test]
    public async Task IDontWant_SkipsPeersSendingRequestedPartialMessages()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            EnablePartialMessages = true,
        });
        _ = setup.Router.GetPartialMessagesTopic(setup.Topic, new PartialMessagesTopicOptions
        {
            RequestPartialMessages = true,
            SupportsSendingPartialMessages = true,
        });
        List<Rpc> partialPeerRpcs = [];
        List<Rpc> fullPeerRpcs = [];
        PeerId partialPeer = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV13, partialPeerRpcs);
        _ = setup.ConnectPeer(4, PubsubRouter.GossipsubProtocolVersionV13, fullPeerRpcs);
        setup.Router.OnRpc(partialPeer, new Rpc
        {
            Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } },
            Subscriptions = { new Rpc.Types.SubOpts { Subscribe = true, Topicid = setup.Topic, SupportsSendingPartial = true } },
        });
        await setup.Router.Heartbeat();
        partialPeerRpcs.Clear();
        fullPeerRpcs.Clear();
        Identity author = TestPeers.Identity(5);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, new byte[1024], author).Publish.Single();

        setup.Router.OnRpc(setup.RemotePeerId, new Rpc { Publish = { message } });

        Assert.Multiple(() =>
        {
            Assert.That(GetIdontwantIds(partialPeerRpcs), Is.Empty);
            Assert.That(GetIdontwantIds(fullPeerRpcs), Is.EqualTo(new[] { ByteString.CopyFrom(PubsubSettings.ConcatFromAndSeqno(message).Bytes) }));
            Assert.That(GetPublishedMessages(partialPeerRpcs), Is.EqualTo(new[] { message }));
            Assert.That(GetPublishedMessages(fullPeerRpcs), Is.EqualTo(new[] { message }));
        });
    }

    [Test]
    public async Task IDontWant_DoesNotAnnounceSmallOrRejectedMessages()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        List<Rpc> senderRpcs = [];
        PeerId sender = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, senderRpcs);
        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        Identity author = TestPeers.Identity(4);

        Message small = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, new byte[1023], author).Publish.Single();
        Message rejected = new Rpc().WithMessages(setup.Topic, 2, author.PeerId.Bytes, new byte[1024], author).Publish.Single();
        setup.Router.OnRpc(sender, new Rpc { Publish = { small } });
        setup.Router.VerifyMessage = _ => MessageValidity.Rejected;
        setup.Router.OnRpc(sender, new Rpc { Publish = { rejected } });

        Assert.That(GetIdontwantIds(setup.SentRpcs), Is.Empty);
    }

    [Test]
    public async Task IDontWant_DoesNotAmplifyLargeMessageIds()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            GetMessageId = message => new MessageId(message.Data.ToByteArray()[..600]),
        });
        List<Rpc> senderRpcs = [];
        PeerId sender = setup.ConnectPeer(3, PubsubRouter.GossipsubProtocolVersionV12, senderRpcs);
        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();
        Identity author = TestPeers.Identity(4);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, new byte[1024], author).Publish.Single();

        setup.Router.OnRpc(sender, new Rpc { Publish = { message } });

        Assert.That(GetIdontwantIds(setup.SentRpcs), Is.Empty);
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.EqualTo(new[] { message }));
    }

    [Test]
    public void Router_RejectsNonPositiveIdontwantThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), new PubsubSettings { IdontwantMessageThreshold = 0 }));
    }

    [Test]
    public async Task IDontWant_SeparatesEnvelopeAndMessageIdLimits()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIdontwantMessages = 1,
            MaxIdontwantLength = 1,
        });
        MessageId first = setup.Publish([1]);
        MessageId second = setup.Publish([2]);
        MessageId third = setup.Publish([3]);
        setup.SentRpcs.Clear();

        Rpc unwanted = new() { Control = new ControlMessage() };
        unwanted.Control.Idontwant.Add(new ControlIDontWant
        {
            MessageIDs = { ByteString.CopyFrom(first.Bytes), ByteString.CopyFrom(second.Bytes) },
        });
        unwanted.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(third.Bytes) } });
        setup.Router.OnRpc(setup.RemotePeerId, unwanted);

        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(first));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(second));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));

        setup.SentRpcs.Clear();
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(third));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EmptyIwantAndIdontwantEnvelopesDoNotBypassTheEnvelopeLimits()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings
        {
            HeartbeatInterval = int.MaxValue,
            MaxIwantMessages = 2,
            MaxIdontwantMessages = 2,
        });
        MessageId iwantMessageId = setup.Publish([1]);
        MessageId idontwantMessageId = setup.Publish([2]);
        setup.SentRpcs.Clear();

        Rpc iwant = new() { Control = new ControlMessage() };
        iwant.Control.Iwant.Add(new ControlIWant());
        iwant.Control.Iwant.Add(new ControlIWant());
        iwant.Control.Iwant.Add(new ControlIWant { MessageIDs = { ByteString.CopyFrom(iwantMessageId.Bytes) } });
        setup.Router.OnRpc(setup.RemotePeerId, iwant);
        Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);

        await setup.Router.Heartbeat();
        setup.SentRpcs.Clear();

        Rpc idontwant = new() { Control = new ControlMessage() };
        idontwant.Control.Idontwant.Add(new ControlIDontWant());
        idontwant.Control.Idontwant.Add(new ControlIDontWant());
        idontwant.Control.Idontwant.Add(new ControlIDontWant { MessageIDs = { ByteString.CopyFrom(idontwantMessageId.Bytes) } });
        setup.Router.OnRpc(setup.RemotePeerId, idontwant);
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(idontwantMessageId));
        Assert.That(GetPublishedMessages(setup.SentRpcs), Has.Count.EqualTo(1));
    }

    [Test]
    public void IwantPromises_AreBoundedAndFulfilledByTheMessageId()
    {
        IwantPromiseTracker tracker = new(maxPromises: 1);
        PeerId firstPeer = TestPeers.PeerId(1);
        PeerId secondPeer = TestPeers.PeerId(2);
        MessageId firstMessage = new([1]);
        MessageId secondMessage = new([2]);

        tracker.Add(firstPeer, [firstMessage], DateTime.UtcNow.AddSeconds(-1));
        tracker.Add(secondPeer, [secondMessage], DateTime.UtcNow.AddSeconds(-1));

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count, Is.EqualTo(1));
            Assert.That(tracker.TakeExpired(DateTime.UtcNow), Is.EquivalentTo(new Dictionary<PeerId, int> { [firstPeer] = 1 }));
            Assert.That(tracker.Count, Is.Zero);
        });

        tracker.Add(firstPeer, [firstMessage], DateTime.UtcNow.AddSeconds(1));
        tracker.Fulfill(firstMessage);
        Assert.That(tracker.Count, Is.Zero);
    }

    [Test]
    public void IwantPromises_RefreshExistingDeadlines()
    {
        IwantPromiseTracker tracker = new(maxPromises: 1);
        PeerId peer = TestPeers.PeerId(1);
        MessageId message = new([1]);
        DateTime now = DateTime.UtcNow;

        tracker.Add(peer, [message], now.AddSeconds(-1));
        tracker.Add(peer, [message], now.AddSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(tracker.Count, Is.EqualTo(1));
            Assert.That(tracker.TakeExpired(now), Is.Empty);
        });
    }

    [Test]
    public async Task UnfulfilledIwantPromises_ReducePeerScoreOnHeartbeatOnlyOnce()
    {
        PubsubSettings settings = new()
        {
            HeartbeatInterval = int.MaxValue,
            DecayInterval = int.MaxValue,
            IWantFollowupTime = 20,
        };
        await using RouterSetup setup = await RouterSetup.Create(settings);
        double initialScore = setup.RemotePeerScore;

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, [1]));
        Assert.That(setup.Router.IwantPromiseCount, Is.EqualTo(1));
        await Task.Delay(settings.IWantFollowupTime + 20);
        await setup.Router.Heartbeat();

        Assert.Multiple(() =>
        {
            Assert.That(setup.Router.IwantPromiseCount, Is.Zero);
            Assert.That(setup.RemotePeerScore, Is.EqualTo(initialScore + settings.BehaviorPenaltyWeight));
        });

        await setup.Router.Heartbeat();
        Assert.That(setup.RemotePeerScore, Is.EqualTo(initialScore + settings.BehaviorPenaltyWeight));
    }

    [TestCase(MessageValidity.Accepted)]
    [TestCase(MessageValidity.Rejected)]
    [TestCase(MessageValidity.Ignored)]
    public async Task ReceivedMessages_FulfillIwantPromises(MessageValidity validity)
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        Identity author = TestPeers.Identity(3);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, [1], author).Publish.Single();
        MessageId messageId = PubsubSettings.ConcatFromAndSeqno(message);

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, messageId.Bytes));
        Assert.That(setup.Router.IwantPromiseCount, Is.EqualTo(1));

        setup.Router.VerifyMessage = _ => validity;
        setup.Router.OnRpc(setup.RemotePeerId, new Rpc { Publish = { message } });

        Assert.That(setup.Router.IwantPromiseCount, Is.Zero);
    }

    [Test]
    public async Task InvalidSignature_KeepsIwantPromiseWithoutAcceptingMessage()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        Identity author = TestPeers.Identity(3);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, [1], author).Publish.Single();
        message.Data = ByteString.CopyFrom([2]);
        Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.False);
        MessageId messageId = PubsubSettings.ConcatFromAndSeqno(message);
        int deliveries = 0;
        setup.Router.OnMessage += (_, _, _) => deliveries++;

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, messageId.Bytes));
        Assert.That(setup.Router.IwantPromiseCount, Is.EqualTo(1));
        setup.SentRpcs.Clear();

        setup.Router.OnRpc(setup.RemotePeerId, new Rpc { Publish = { message } });
        setup.Router.OnRpc(setup.RemotePeerId, CreateIwant(messageId));

        Assert.Multiple(() =>
        {
            Assert.That(setup.Router.IwantPromiseCount, Is.EqualTo(1), "An invalid signature must not fulfill the promise.");
            Assert.That(deliveries, Is.Zero);
            Assert.That(GetPublishedMessages(setup.SentRpcs), Is.Empty);
            Assert.That(setup.RemotePeerScore, Is.LessThan(0), "Invalid delivery must still be penalized.");
        });
    }

    [Test]
    public async Task ThrottledMessages_ClearOutstandingIwantPromises()
    {
        await using RouterSetup setup = await RouterSetup.Create(new PubsubSettings { HeartbeatInterval = int.MaxValue });
        Identity author = TestPeers.Identity(3);
        Message message = new Rpc().WithMessages(setup.Topic, 1, author.PeerId.Bytes, [1], author).Publish.Single();
        MessageId messageId = PubsubSettings.ConcatFromAndSeqno(message);

        setup.Router.OnRpc(setup.RemotePeerId, CreateIhave(setup.Topic, messageId.Bytes));
        Assert.That(setup.Router.IwantPromiseCount, Is.EqualTo(1));

        setup.Router.VerifyMessage = _ => MessageValidity.Throttled;
        setup.Router.OnRpc(setup.RemotePeerId, new Rpc { Publish = { message } });

        Assert.That(setup.Router.IwantPromiseCount, Is.Zero);
    }

    [TestCase(0, 1, 1, 1, 1, 1, 1, 1)]
    [TestCase(1, 0, 1, 1, 1, 1, 1, 1)]
    [TestCase(1, 1, 0, 1, 1, 1, 1, 1)]
    [TestCase(1, 1, 1, 0, 1, 1, 1, 1)]
    [TestCase(1, 1, 1, 1, 0, 1, 1, 1)]
    [TestCase(1, 1, 1, 1, 1, 0, 1, 1)]
    [TestCase(1, 1, 1, 1, 1, 1, 0, 1)]
    [TestCase(1, 1, 1, 1, 1, 1, 1, 0)]
    public void Router_RejectsInvalidControlLimits(
        int maxIhaveMessages,
        int maxIhaveLength,
        int retransmission,
        int responseBytes,
        int promises,
        int followupTime,
        int idontwantTtl,
        int maxIdontwantMessages)
    {
        PubsubSettings settings = new()
        {
            MaxIHaveMessages = maxIhaveMessages,
            MaxIHaveLength = maxIhaveLength,
            GossipRetransmission = retransmission,
            MaxIwantResponseBytes = responseBytes,
            MaxIwantPromises = promises,
            IWantFollowupTime = followupTime,
            IdontwantTtlHeartbeats = idontwantTtl,
            MaxIdontwantMessages = maxIdontwantMessages,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), settings));
    }

    [TestCase(0, 1, 1)]
    [TestCase(1, 0, 1)]
    [TestCase(1, 1, 0)]
    public void Router_RejectsInvalidControlEnvelopeLimits(int maxIwantMessages, int maxIwantLength, int maxIdontwantLength)
    {
        PubsubSettings settings = new()
        {
            MaxIwantMessages = maxIwantMessages,
            MaxIwantLength = maxIwantLength,
            MaxIdontwantLength = maxIdontwantLength,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), settings));
    }

    private static Rpc CreateIhave(string topic, params byte[][] ids)
    {
        Rpc rpc = new() { Control = new ControlMessage() };
        ControlIHave ihave = new() { TopicID = topic };
        ihave.MessageIDs.AddRange(ids.Select(ByteString.CopyFrom));
        rpc.Control.Ihave.Add(ihave);
        return rpc;
    }

    private static Rpc CreateIwant(params MessageId[] ids)
    {
        Rpc rpc = new() { Control = new ControlMessage() };
        rpc.Control.Iwant.Add(new ControlIWant { MessageIDs = { ids.Select(id => ByteString.CopyFrom(id.Bytes)) } });
        return rpc;
    }

    private static IReadOnlyList<ByteString> GetIwantIds(IEnumerable<Rpc> rpcs) => rpcs
        .Where(rpc => rpc.Control is not null)
        .SelectMany(rpc => rpc.Control.Iwant)
        .SelectMany(iwant => iwant.MessageIDs)
        .ToArray();

    private static IReadOnlyList<ByteString> GetIdontwantIds(IEnumerable<Rpc> rpcs) => rpcs
        .Where(rpc => rpc.Control is not null)
        .SelectMany(rpc => rpc.Control.Idontwant)
        .SelectMany(idontwant => idontwant.MessageIDs)
        .ToArray();

    private static IReadOnlyList<Message> GetPublishedMessages(IEnumerable<Rpc> rpcs) => rpcs
        .SelectMany(rpc => rpc.Publish)
        .ToArray();

    private sealed class RouterSetup : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource connection = new();
        private readonly List<TaskCompletionSource> extraConnections = [];

        private RouterSetup(PubsubRouter router, string topic, PeerId remotePeerId, List<Rpc> sentRpcs)
        {
            Router = router;
            Topic = topic;
            RemotePeerId = remotePeerId;
            SentRpcs = sentRpcs;
        }

        public PubsubRouter Router { get; }
        public string Topic { get; }
        public PeerId RemotePeerId { get; }
        public List<Rpc> SentRpcs { get; }
        public double RemotePeerScore => Router.GetPeerScore(RemotePeerId);

        public static async Task<RouterSetup> Create(PubsubSettings settings)
        {
            const string topic = "topic";
            PubsubRouter router = new(new PeerStore(), settings);
            ILocalPeer localPeer = Substitute.For<ILocalPeer>();
            localPeer.Identity.Returns(TestPeers.Identity(1));
            localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());

            RouterSetup setup = new(router, topic, TestPeers.PeerId(2), []);
            await router.StartAsync(localPeer, setup.cancellation.Token);
            _ = router.GetTopic(topic);
            Multiaddress remoteAddress = TestPeers.Multiaddr(2);
            router.OutboundConnection(remoteAddress, PubsubRouter.GossipsubProtocolVersionV12, setup.connection.Task, setup.SentRpcs.Add);
            router.OnRpc(setup.RemotePeerId, new Rpc().WithTopics([topic], []));
            setup.SentRpcs.Clear();
            return setup;
        }

        public MessageId Publish(byte[] data)
        {
            Router.Publish(Topic, data);
            Message published = SentRpcs.Last().Publish.Single();
            return PubsubSettings.ConcatFromAndSeqno(published);
        }

        public PeerId ConnectPeer(int index, string protocol, List<Rpc> sentRpcs)
        {
            PeerId peerId = TestPeers.PeerId(index);
            TaskCompletionSource connection = new();
            extraConnections.Add(connection);
            Router.OutboundConnection(TestPeers.Multiaddr(index), protocol, connection.Task, sentRpcs.Add);
            Router.OnRpc(peerId, new Rpc().WithTopics([Topic], []));
            return peerId;
        }

        public ValueTask DisposeAsync()
        {
            connection.TrySetResult();
            foreach (TaskCompletionSource extraConnection in extraConnections) extraConnection.TrySetResult();
            cancellation.Cancel();
            cancellation.Dispose();
            Router.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
