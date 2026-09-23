// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PartialMessagesTests
{
    [Test]
    public void PartialMessagesTopics_RequireExplicitRouterAndTopicOptIn()
    {
        PubsubRouter disabledRouter = new(new PeerStore());
        Assert.That(
            () => disabledRouter.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }),
            Throws.TypeOf<InvalidOperationException>());

        PubsubRouter enabledRouter = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        Assert.That(
            () => enabledRouter.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions { RequestPartialMessages = true }),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void GetTopic_DoesNotExposePartialMessagesWithoutTheExplicitApi()
    {
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        ITopic topic = router.GetTopic("topic");

        IPartialMessagesTopic partialTopic = router.GetPartialMessagesTopic(
            "topic",
            new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true });

        Assert.Multiple(() =>
        {
            Assert.That(topic, Is.Not.InstanceOf<IPartialMessagesTopic>());
            Assert.That(partialTopic, Is.Not.SameAs(topic));
            Assert.That(partialTopic.IsSubscribed, Is.True);
        });
    }

    [Test]
    public void PartialMessageGossipCache_IsBoundedAndExpiresGroups()
    {
        PartialMessageGossipCache cache = new(maxGroupsPerTopic: 2, groupTtlHeartbeats: 2);

        cache.Track("topic", [1]);
        cache.Track("topic", [2]);
        cache.Track("topic", [3]);

        Assert.That(cache.GetGroupIds("topic"), Is.EqualTo(new[] { new byte[] { 2 }, new byte[] { 3 } }));

        cache.Heartbeat();
        Assert.That(cache.GetGroupIds("topic"), Has.Count.EqualTo(2));

        cache.Heartbeat();
        Assert.That(cache.GetGroupIds("topic"), Is.Empty);
    }

    [Test]
    public async Task PartialMessages_ExtensionsAreAttachedToTheFirstConcurrentRpc()
    {
        PubsubRouter.PubsubPeer peer = new(
            TestPeers.PeerId(1),
            PubsubRouter.GossipsubProtocolVersionV13,
            logger: null,
            settings: new PubsubSettings { EnablePartialMessages = true },
            reconnectionPolicy: new PubsubRouter.ReconnectionPolicy());
        ConcurrentQueue<Rpc> sentRpcs = [];
        peer.SendRpc = sentRpcs.Enqueue;

        using ManualResetEventSlim start = new(initialState: false);
        Task[] sends = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                peer.Send(new Rpc());
            }))
            .ToArray();
        start.Set();
        await Task.WhenAll(sends);

        Assert.That(sentRpcs.TryPeek(out Rpc? first), Is.True);
        Assert.That(first!.Control?.Extensions?.PartialMessages, Is.True);
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
                SupportsSendingPartialMessages = true,
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

        await router.Heartbeat();
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
            new PartialMessagesTopicOptions { RequestPartialMessages = true, SupportsSendingPartialMessages = true });
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
                TopicID = ByteString.CopyFromUtf8(topicName),
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

        received = null;
        router.OnRpc(remotePeerId, new Rpc
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8(topicName),
                PartialMessage = ByteString.CopyFrom([2]),
            },
        });
        router.OnRpc(remotePeerId, new Rpc
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8(topicName),
                GroupID = ByteString.CopyFrom([1]),
            },
        });

        Assert.That(received, Is.Null);

        connection.SetResult();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PartialMessages_RequestingImpliesSendingSupport(bool omitSendingFlag)
    {
        const string topicName = "topic";
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic(
            topicName,
            new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true });
        Multiaddress remoteAddress = TestPeers.Multiaddr(1);
        PeerId remotePeerId = remoteAddress.GetPeerId()!;
        TaskCompletionSource connection = new();
        List<Rpc> sentRpcs = [];
        router.OutboundConnection(remoteAddress, PubsubRouter.GossipsubProtocolVersionV13, connection.Task, sentRpcs.Add);
        Rpc subscription = CreateSubscriptionRpc(topicName, requestsPartialMessages: true, supportsSendingPartialMessages: false);
        if (omitSendingFlag)
        {
            subscription.Subscriptions.Single().ClearSupportsSendingPartial();
        }
        router.OnRpc(remotePeerId, subscription);
        sentRpcs.Clear();

        topic.SendPartial(remotePeerId, [1], partialMessage: [2], partsMetadata: [3]);

        PartialMessagesExtension sentPartial = sentRpcs.Single().Partial;
        Assert.Multiple(() =>
        {
            Assert.That(sentPartial.PartialMessage.ToByteArray(), Is.EqualTo(new byte[] { 2 }));
            Assert.That(sentPartial.PartsMetadata.ToByteArray(), Is.EqualTo(new byte[] { 3 }));
        });
        connection.SetResult();
    }

    [Test]
    public void PartialMessages_AreNeverAdvertisedOnOlderProtocols()
    {
        PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        _ = router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions
        {
            RequestPartialMessages = true,
            SupportsSendingPartialMessages = true,
        });
        List<Rpc> sentRpcs = [];
        TaskCompletionSource connection = new();

        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV12, connection.Task, sentRpcs.Add);

        Rpc.Types.SubOpts subscription = sentRpcs.Single().Subscriptions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(sentRpcs.Single().Control, Is.Null);
            Assert.That(subscription.HasRequestsPartial, Is.False);
            Assert.That(subscription.HasSupportsSendingPartial, Is.False);
        });
        connection.SetResult();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PartialMessages_GossipUsesTheApplicationCallbackInsteadOfIhave(bool throwingHandler)
    {
        const string topicName = "topic";
        PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            EnablePartialMessages = true,
            HeartbeatInterval = int.MaxValue,
            Degree = 1,
            LowestDegree = 1,
            LazyDegree = 1,
        });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic(topicName, new PartialMessagesTopicOptions
        {
            RequestPartialMessages = true,
            SupportsSendingPartialMessages = true,
        });
        using CancellationTokenSource cancellation = new();
        await router.StartAsync(new LocalPeerStub(), cancellation.Token);

        TaskCompletionSource connection = new();
        Dictionary<PeerId, List<Rpc>> sentRpcs = [];
        List<PeerId>? partialGossipRecipients = null;
        byte[]? partialGossipGroupId = null;
        if (throwingHandler)
        {
            router.OnPartialGossip += (_, _, _) => throw new InvalidOperationException("Application failure");
        }
        int notifications = 0;
        router.OnPartialGossip += (topicId, groupId, peers) =>
        {
            notifications++;
            Assert.That(topicId, Is.EqualTo(topicName));
            partialGossipGroupId = groupId;
            partialGossipRecipients = peers.ToList();
        };

        foreach (int peerNumber in Enumerable.Range(1, 3))
        {
            Multiaddress address = TestPeers.Multiaddr(peerNumber);
            PeerId peerId = address.GetPeerId()!;
            List<Rpc> peerRpcs = [];
            sentRpcs.Add(peerId, peerRpcs);
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV13, connection.Task, peerRpcs.Add);
            router.OnRpc(peerId, CreateSubscriptionRpc(topicName, requestsPartialMessages: true, supportsSendingPartialMessages: true));
        }

        foreach (List<Rpc> peerRpcs in sentRpcs.Values)
        {
            peerRpcs.Clear();
        }

        await router.Heartbeat();
        foreach (List<Rpc> peerRpcs in sentRpcs.Values)
        {
            peerRpcs.Clear();
        }

        topic.PublishPartial([7, 8], partialMessage: [1, 2, 3]);
        foreach (List<Rpc> peerRpcs in sentRpcs.Values)
        {
            peerRpcs.Clear();
        }

        await router.Heartbeat();

        Assert.That(partialGossipRecipients, Is.Not.Null.And.Not.Empty);
        Assert.That(partialGossipGroupId, Is.EqualTo(new byte[] { 7, 8 }));
        Assert.That(partialGossipRecipients!.All(sentRpcs.ContainsKey), Is.True);
        Assert.That(sentRpcs.Values.SelectMany(rpcs => rpcs).SelectMany(rpc => rpc.Control?.Ihave ?? []), Is.Empty);

        await router.Heartbeat();
        Assert.That(notifications, Is.EqualTo(2), "A failed handler must not interrupt this or subsequent heartbeats.");

        cancellation.Cancel();
        connection.SetResult();
    }

    [Test]
    public async Task PartialMessages_RequestersDoNotReceiveFullPublishesOrRelays()
    {
        const string topicName = "topic";
        PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            EnablePartialMessages = true,
            HeartbeatInterval = int.MaxValue,
            Degree = 3,
            LowestDegree = 3,
            HighestDegree = 3,
        });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic(topicName, new PartialMessagesTopicOptions
        {
            SupportsSendingPartialMessages = true,
        });
        using CancellationTokenSource cancellation = new();
        await router.StartAsync(new LocalPeerStub(), cancellation.Token);

        TaskCompletionSource connection = new();
        Dictionary<PeerId, List<Rpc>> sentRpcs = [];
        foreach (int peerNumber in Enumerable.Range(1, 3))
        {
            Multiaddress address = TestPeers.Multiaddr(peerNumber);
            PeerId peerId = address.GetPeerId()!;
            List<Rpc> peerRpcs = [];
            sentRpcs.Add(peerId, peerRpcs);
            router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV13, connection.Task, peerRpcs.Add);
            router.OnRpc(peerId, CreateSubscriptionRpc(
                topicName,
                requestsPartialMessages: peerNumber == 1,
                supportsSendingPartialMessages: true));
        }

        await router.Heartbeat();
        foreach (List<Rpc> peerRpcs in sentRpcs.Values)
        {
            peerRpcs.Clear();
        }

        topic.Publish([1]);

        Assert.Multiple(() =>
        {
            Assert.That(sentRpcs[TestPeers.PeerId(1)].SelectMany(rpc => rpc.Publish), Is.Empty);
            Assert.That(sentRpcs[TestPeers.PeerId(2)].SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(1));
        });

        foreach (List<Rpc> peerRpcs in sentRpcs.Values)
        {
            peerRpcs.Clear();
        }

        Identity author = TestPeers.Identity(4);
        router.OnRpc(TestPeers.PeerId(3), new Rpc().WithMessages(topicName, 1, author.PeerId.Bytes, [2], author));

        Assert.Multiple(() =>
        {
            Assert.That(sentRpcs[TestPeers.PeerId(1)].SelectMany(rpc => rpc.Publish), Is.Empty);
            Assert.That(sentRpcs[TestPeers.PeerId(2)].SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(1));
        });

        cancellation.Cancel();
        connection.SetResult();
    }

    [Test]
    public void PartialMessages_TopicDeliveryRequiresSubscriptionAndCapabilities()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic("topic",
            new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true });
        TaskCompletionSource connection = new();
        PeerId peer = TestPeers.PeerId(1);
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });
        router.OnRpc(peer, CreateSubscriptionRpc("topic", true, true));
        int deliveries = 0;
        topic.OnPartialMessage += (_, _) => deliveries++;
        Rpc metadata = new()
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8("topic"),
                GroupID = ByteString.CopyFrom([1]),
                PartsMetadata = ByteString.CopyFrom([2]),
            },
        };

        router.OnRpc(peer, metadata);
        Assert.That(deliveries, Is.EqualTo(1));
        topic.Unsubscribe();
        router.OnRpc(peer, metadata);
        Assert.That(deliveries, Is.EqualTo(1));
        topic.Subscribe();
        router.OnRpc(peer, metadata);
        Assert.That(deliveries, Is.EqualTo(2));
        router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions());
        router.OnRpc(peer, metadata);
        Assert.That(deliveries, Is.EqualTo(2));
        connection.SetResult();
    }

    [TestCase("metadata-only")]
    [TestCase("ordinary")]
    [TestCase("unknown")]
    public async Task PartialMessages_DataWithoutARequestIsDroppedAndPenalized(string mode)
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        if (mode == "ordinary")
        {
            router.GetTopic("topic");
        }
        else if (mode != "unknown")
        {
            router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions
            {
                SupportsSendingPartialMessages = true,
            });
        }
        TaskCompletionSource connection = new();
        PeerId peer = TestPeers.PeerId(1);
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });
        router.OnRpc(peer, CreateSubscriptionRpc("topic", true, true));
        IRoutingStateContainer state = router;
        await router.Heartbeat();
        int deliveries = 0;
        router.OnPartialMessage += (_, _, _) => deliveries++;
        router.OnRpc(peer, new Rpc
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8("topic"),
                GroupID = ByteString.CopyFrom([1]),
                PartialMessage = ByteString.CopyFrom([2]),
            },
        });

        Assert.That(deliveries, Is.Zero);
        // A behavior penalty makes the peer ineligible for grafting.
        router.GetTopic("topic");
        await router.Heartbeat();
        Assert.That(state.Mesh["topic"], Does.Not.Contain(peer));
        connection.SetResult();
    }

    [Test]
    public void PartialMessages_InFlightDataAfterUnsubscribeIsDroppedWithoutPenalty()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions
        {
            RequestPartialMessages = true,
            SupportsSendingPartialMessages = true,
        });
        TaskCompletionSource connection = new();
        PeerId peer = TestPeers.PeerId(1);
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });
        router.OnRpc(peer, new Rpc { Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } } });
        int deliveries = 0;
        topic.OnPartialMessage += (_, _) => deliveries++;
        topic.Unsubscribe();
        double scoreBefore = (double)typeof(PubsubRouter)
            .GetMethod("GetPeerScore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(router, [peer])!;

        router.OnRpc(peer, new Rpc
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8("topic"),
                GroupID = ByteString.CopyFrom([1]),
                PartialMessage = ByteString.CopyFrom([2]),
            },
        }, isFirstRpc: false);

        double scoreAfter = (double)typeof(PubsubRouter)
            .GetMethod("GetPeerScore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(router, [peer])!;
        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Is.Zero);
            Assert.That(scoreAfter, Is.EqualTo(scoreBefore));
        });
        connection.SetResult();
    }

    [Test]
    public void PartialMessages_HandlerCanWaitForRouterWorkOnAnotherThread()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { EnablePartialMessages = true });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic("topic", new PartialMessagesTopicOptions
        {
            RequestPartialMessages = true,
            SupportsSendingPartialMessages = true,
        });
        TaskCompletionSource connection = new();
        PeerId peer = TestPeers.PeerId(1);
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });
        router.OnRpc(peer, new Rpc { Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } } });
        bool? heldRouterLock = null;
        bool heartbeatCompleted = false;
        topic.OnPartialMessage += (_, _) =>
        {
            heldRouterLock = Monitor.IsEntered(router);
            Task heartbeat = Task.Run(((IRoutingStateContainer)router).Heartbeat);
            heartbeatCompleted = heartbeat.Wait(TimeSpan.FromSeconds(5));
        };

        router.OnRpc(peer, new Rpc
        {
            Partial = new PartialMessagesExtension
            {
                TopicID = ByteString.CopyFromUtf8("topic"),
                GroupID = ByteString.CopyFrom([1]),
                PartsMetadata = ByteString.CopyFrom([2]),
            },
        }, isFirstRpc: false);

        Assert.Multiple(() =>
        {
            Assert.That(heldRouterLock, Is.False);
            Assert.That(heartbeatCompleted, Is.True);
        });
        connection.SetResult();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PublishPartial_UsesFanoutOnlyWhenUnsubscribed(bool subscribed)
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            EnablePartialMessages = true,
            HeartbeatInterval = int.MaxValue,
        });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic("topic",
            new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: subscribed);
        using CancellationTokenSource cancellation = new();
        await router.StartAsync(new LocalPeerStub(), cancellation.Token);
        TaskCompletionSource connection = new();
        List<Rpc> sent = [];
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, connection.Task, sent.Add);
        router.OnRpc(TestPeers.PeerId(1), CreateSubscriptionRpc("topic", true, false));
        sent.Clear();

        topic.PublishPartial([1], partialMessage: [2]);

        IRoutingStateContainer state = router;
        Assert.Multiple(() =>
        {
            Assert.That(state.Fanout.ContainsKey("topic"), Is.EqualTo(!subscribed));
            Assert.That(state.FanoutLastPublished.ContainsKey("topic"), Is.EqualTo(!subscribed));
            Assert.That(sent.Count, Is.EqualTo(subscribed ? 0 : 1));
        });
        cancellation.Cancel();
        connection.SetResult();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PublishPartial_CanRunConcurrentlyWithHeartbeatAndSubscriptions(bool subscribed)
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            EnablePartialMessages = true,
            HeartbeatInterval = int.MaxValue,
        });
        IPartialMessagesTopic topic = router.GetPartialMessagesTopic("topic",
            new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: subscribed);
        using CancellationTokenSource cancellation = new();
        await router.StartAsync(new LocalPeerStub(), cancellation.Token);
        TaskCompletionSource connection = new();
        PeerId peer = TestPeers.PeerId(1);
        router.OutboundConnection(TestPeers.Multiaddr(1), PubsubRouter.GossipsubProtocolVersionV13, connection.Task, _ => { });
        router.OnRpc(peer, CreateSubscriptionRpc("topic", true, true));
        using Barrier start = new(2);
        Task publisher = Task.Run(() =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 500; i++)
            {
                topic.PublishPartial([1], partialMessage: [2]);
            }
        });
        Task heartbeat = Task.Run(async () =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 500; i++)
            {
                router.OnRpc(peer, new Rpc().WithTopics([], ["topic"]));
                router.OnRpc(peer, new Rpc().WithTopics(["topic"], []));
                await router.Heartbeat();
            }
        });

        await Task.WhenAll(publisher, heartbeat).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.That(((IRoutingStateContainer)router).Fanout.ContainsKey("topic"), Is.EqualTo(!subscribed));
        cancellation.Cancel();
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
