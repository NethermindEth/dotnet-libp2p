// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class SubscriptionAdmissionTests
{
    [Test]
    public void UnwantedMessagesDoNotDeliverRelayScoreOrReserveIds()
    {
        using PubsubRouter router = new(new PeerStore());
        List<Rpc> forwarded = [];
        Connect(router, 1);
        Connect(router, 2, forwarded.Add);
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["other"], []));
        forwarded.Clear();
        List<string> deliveries = [];
        router.OnMessage += (topic, _, _) => deliveries.Add(topic);
        Rpc message = Signed("other", 1);

        router.OnRpc(TestPeers.PeerId(1), message);

        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Is.Empty);
            Assert.That(forwarded.SelectMany(rpc => rpc.Publish), Is.Empty);
            Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.Zero);
            Assert.That(router.PendingValidationCount, Is.Zero);
        });
        router.GetTopic("other");
        router.OnRpc(TestPeers.PeerId(1), message);
        router.OnRpc(TestPeers.PeerId(1), message);
        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Is.EqualTo(new[] { "other" }), "An unwanted ID must remain available after subscribing; wanted traffic still deduplicates.");
            Assert.That(forwarded.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(1));
            Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.GreaterThan(0));
        });
    }

    [Test]
    public void UnwantedTrafficCannotBuyPeerExchangeCredit()
    {
        PubsubSettings settings = new();
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        for (int i = 0; i < 300; i++)
        {
            router.OnRpc(TestPeers.PeerId(1), Signed($"other-{i % 4}", i));
        }

        Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.Zero,
            $"Unwanted deliveries must not approach AcceptPXThreshold ({settings.AcceptPXThreshold}).");
    }

    [TestCase(PubsubRouter.FloodsubProtocolVersion)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11)]
    public void RemoteUnsubscribeAndDisconnectReleaseTopicTables(string protocol)
    {
        using PubsubRouter router = new(new PeerStore());
        IRoutingStateContainer state = router;
        TaskCompletionSource closed = Connect(router, 1, protocol: protocol);
        var table = protocol == PubsubRouter.FloodsubProtocolVersion ? state.FloodsubPeers : state.GossipsubPeers;
        string[] topics = Enumerable.Range(0, 200).Select(i => $"remote-{i}").ToArray();
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(topics, []));
        Assert.That(table.Count, Is.EqualTo(topics.Length));
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], topics.Append("unknown")));
        Assert.That(table, Is.Empty, "Unsubscribe must remove empty keys and never allocate unknown topics.");
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(topics, []));
        closed.SetResult();
        Assert.That(SpinWait.SpinUntil(() => !state.ConnectedPeers.Any(), TimeSpan.FromSeconds(5)), Is.True);
        lock (router)
        {
            Assert.That(table, Is.Empty, "Disconnect must remove all retained memberships.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void OnlyLocalSubscriptionAdmitsIdsAndValidators(bool direct)
    {
        int ids = 0;
        List<string> validated = [];
        PubsubSettings settings = new();
        settings.DirectPeers = direct ? [TestPeers.Multiaddr(1)] : [];
        settings.TopicScoreParams["configured"] = new TopicScoreParams();
        settings.GetMessageId = message =>
        {
            ids++;
            return PubsubSettings.ConcatFromAndSeqno(message);
        };
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        router.GetTopic("handle", subscribe: false);
        ILocalPeer local = Substitute.For<ILocalPeer>();
        local.Identity.Returns(TestPeers.Identity(9));
        local.ListenAddresses.Returns([TestPeers.Multiaddr(9)]);
        _ = router.StartAsync(local);
        router.Publish("published", [1]);
        ids = 0;
        router.GetTopic("wanted");
        router.VerifyMessage = (_, message) =>
        {
            validated.Add(message.Topic);
            return MessageValidity.Accepted;
        };
        List<string> deliveries = [];
        router.OnMessage += (topic, _, _) => deliveries.Add(topic);
        Rpc mixed = new();
        foreach ((string topic, int i) in new[] { "unknown", "wanted", "configured", "handle", "published", "wanted" }.Select((t, i) => (t, i)))
        {
            mixed.Publish.Add(Signed(topic, i).Publish[0]);
        }
        Message invalid = Signed("unknown", 100).Publish[0];
        invalid.Signature = ByteString.CopyFrom([0]);
        mixed.Publish.Add(invalid);
        mixed.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "remote", Subscribe = true });
        router.OnRpc(TestPeers.PeerId(1), mixed);
        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.EqualTo(2));
            Assert.That(validated, Is.EqualTo(new[] { "wanted", "wanted" }));
            Assert.That(deliveries, Is.EqualTo(validated));
            Assert.That(((IRoutingStateContainer)router).FloodsubPeers["remote"], Has.Member(TestPeers.PeerId(1)));
        });
    }

    [Test]
    public void UnwantedTrafficDoesNotEvictWantedDeduplicationOrMessageCache()
    {
        PubsubSettings settings = new() { MaxSeenMessageIds = 1, MaxMessageCacheEntries = 1 };
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        List<Rpc> responses = [];
        Connect(router, 2, responses.Add, PubsubRouter.GossipsubProtocolVersionV11);
        router.GetTopic("wanted");
        int deliveries = 0;
        router.OnMessage += (_, _, _) => deliveries++;
        Rpc wanted = Signed("wanted", 1);
        router.OnRpc(TestPeers.PeerId(1), wanted);
        router.OnRpc(TestPeers.PeerId(1), Signed("unwanted", 2));
        router.OnRpc(TestPeers.PeerId(1), wanted);
        responses.Clear();
        router.OnRpc(TestPeers.PeerId(2), new Rpc
        {
            Control = new ControlMessage { Iwant = { new ControlIWant { MessageIDs = { ByteString.CopyFrom(settings.GetMessageId(wanted.Publish[0]).Bytes) } } } }
        });
        Assert.Multiple(() =>
        {
            Assert.That(deliveries, Is.EqualTo(1));
            Assert.That(responses.SelectMany(rpc => rpc.Publish), Is.EqualTo(wanted.Publish));
        });
    }

    [Test]
    public void UnsubscribeDuringIdCalculationDropsBeforeValidation()
    {
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        PubsubSettings settings = new();
        settings.GetMessageId = message =>
        {
            entered.Set();
            release.Wait();
            return PubsubSettings.ConcatFromAndSeqno(message);
        };
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        router.GetTopic("wanted");
        int validations = 0;
        router.VerifyMessage = (_, _) => { validations++; return MessageValidity.Accepted; };
        Task receive = Task.Run(() => router.OnRpc(TestPeers.PeerId(1), Signed("wanted", 1)));
        try
        {
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            router.Unsubscribe("wanted");
        }
        finally
        {
            release.Set();
            Assert.That(receive.Wait(TimeSpan.FromSeconds(5)), Is.True);
        }
        Assert.That(validations, Is.Zero);
        Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.Zero);
    }

    [TestCase(MessageValidity.Accepted)]
    [TestCase(MessageValidity.Rejected)]
    [TestCase(MessageValidity.Ignored)]
    [TestCase(MessageValidity.Deferred)]
    public void ValidatorUnsubscribeDropsAllSideEffects(MessageValidity verdict)
    {
        using PubsubRouter router = new(new PeerStore());
        Connect(router, 1);
        router.GetTopic("wanted");
        int callbacks = 0;
        router.OnMessage += (_, _, _) => callbacks++;
        router.OnDeferredMessage = (_, _) => { callbacks++; return Task.CompletedTask; };
        router.VerifyMessage = (_, _) => { router.Unsubscribe("wanted"); return verdict; };
        Rpc message = Signed("wanted", 1);
        router.OnRpc(TestPeers.PeerId(1), message);
        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.Zero);
            Assert.That(router.PendingValidationCount, Is.Zero);
            Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.Zero);
        });
        router.VerifyMessage = null;
        router.Subscribe("wanted");
        router.OnRpc(TestPeers.PeerId(1), message);
        Assert.That(callbacks, Is.EqualTo(1), "Unsubscribe during validation must not reserve the message ID.");
    }

    [Test]
    public void UnsubscribeReleasesDeferredBytesAndInvalidatesOldCompletion()
    {
        PubsubSettings settings = new() { MaxPendingValidationBytes = Signed("wanted", 1).Publish[0].CalculateSize() };
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        router.GetTopic("wanted");
        router.VerifyMessage = (_, _) => MessageValidity.Deferred;
        TaskCompletionSource work = new();
        router.OnDeferredMessage = (_, _) => work.Task;
        Rpc first = Signed("wanted", 1);
        router.OnRpc(TestPeers.PeerId(1), first);
        Assert.That(router.PendingValidationCount, Is.EqualTo(1));
        router.Unsubscribe("wanted");
        Assert.That(router.PendingValidationCount, Is.Zero);
        router.Subscribe("wanted");
        Assert.That(router.CompleteValidation(first.Publish[0], MessageValidity.Accepted), Is.False);
        Rpc second = Signed("wanted", 2);
        router.OnRpc(TestPeers.PeerId(1), second);
        Assert.That(router.PendingValidationCount, Is.EqualTo(1), "Unsubscribe must release the byte budget.");
        Assert.That(router.CompleteValidation(second.Publish[0], MessageValidity.Accepted), Is.True);
        work.SetResult();
    }

    [TestCase(MessageValidity.Accepted)]
    [TestCase(MessageValidity.Rejected)]
    [TestCase(MessageValidity.Ignored)]
    [TestCase(MessageValidity.Deferred)]
    [TestCase(MessageValidity.Throttled)]
    public void UnwantedPayloadCannotFulfillPromisesOrReachDeferredWork(MessageValidity verdict)
    {
        using PubsubRouter router = new(new PeerStore());
        Connect(router, 1, protocol: PubsubRouter.GossipsubProtocolVersionV11);
        router.Subscribe("wanted");
        Rpc message = Signed("wanted", 1);
        MessageId id = PubsubSettings.ConcatFromAndSeqno(message.Publish[0]);
        router.OnRpc(TestPeers.PeerId(1), new Rpc
        {
            Control = new ControlMessage { Ihave = { new ControlIHave { TopicID = "wanted", MessageIDs = { ByteString.CopyFrom(id.Bytes) } } } }
        });
        Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
        router.Unsubscribe("wanted");
        int callbacks = 0;
        router.VerifyMessage = (_, _) => { callbacks++; return verdict; };
        router.OnDeferredMessage = (_, _) => { callbacks++; return Task.CompletedTask; };
        router.OnRpc(TestPeers.PeerId(1), message);
        Assert.Multiple(() =>
        {
            Assert.That(callbacks, Is.Zero);
            Assert.That(router.PendingValidationCount, Is.Zero);
            Assert.That(router.IwantPromiseCount, Is.EqualTo(1));
            Assert.That(router.GetPeerScore(TestPeers.PeerId(1)), Is.Zero);
        });
    }

    private static Rpc Signed(string topic, long sequence) => new Rpc().WithMessages(
        topic, (ulong)sequence, TestPeers.PeerId(3).Bytes, [1, 2, 3], TestPeers.Identity(3));

    private static TaskCompletionSource Connect(PubsubRouter router, int index, Action<Rpc>? send = null,
        string protocol = PubsubRouter.FloodsubProtocolVersion)
    {
        TaskCompletionSource closed = new();
        router.OutboundConnection(TestPeers.Multiaddr(index), protocol, closed.Task, send ?? (_ => { }));
        return closed;
    }
}
