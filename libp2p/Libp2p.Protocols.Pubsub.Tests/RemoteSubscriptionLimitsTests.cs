// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class RemoteSubscriptionLimitsTests
{
    [TestCase("peer-count")]
    [TestCase("peer-bytes")]
    [TestCase("total-count")]
    [TestCase("total-bytes")]
    public void EveryBudgetRejectsOneOverAndRecoversInWireOrder(string budget)
    {
        PubsubSettings settings = new();
        switch (budget)
        {
            case "peer-count": settings.MaxRemoteSubscriptionsPerPeer = 2; break;
            case "peer-bytes": settings.MaxRemoteSubscriptionTopicBytesPerPeer = 4; break;
            case "total-count": settings.MaxRemoteSubscriptions = 2; break;
            case "total-bytes": settings.MaxRemoteSubscriptionTopicBytes = 4; break;
        }
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["a", "b", "c"], []));
        AssertUsage(router, 2, 4);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers.Keys, Is.EquivalentTo(new[] { "a", "b" }));
        // Duplicates cost nothing; an over-budget addition does not skip a later removal or addition.
        Rpc mixed = new Rpc().WithTopics(["b", "c"], []);
        mixed.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "a", Subscribe = false });
        mixed.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "c", Subscribe = true });
        router.OnRpc(TestPeers.PeerId(1), mixed);
        AssertUsage(router, 2, 4);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers.Keys, Is.EquivalentTo(new[] { "b", "c" }));
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], ["b", "c", "unknown"]));
        AssertUsage(router, 0, 0);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void AggregateBudgetsChargeSharedTopicsForEachPeer(bool bytes)
    {
        PubsubSettings settings = new();
        if (bytes) settings.MaxRemoteSubscriptionTopicBytes = 4;
        else settings.MaxRemoteSubscriptions = 2;
        using PubsubRouter router = new(new PeerStore(), settings);
        TaskCompletionSource firstClosed = Connect(router, 1);
        Connect(router, 2);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["a"], []));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["a", "b"], []));
        AssertUsage(router, 2, 4);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers["a"], Has.Count.EqualTo(2));
        firstClosed.SetResult();
        Assert.That(SpinWait.SpinUntil(() => router.RemoteSubscriptionUsage.Count == 1, TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers["a"], Is.EquivalentTo(new[] { TestPeers.PeerId(2) }));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["b"], []));
        AssertUsage(router, 2, 4);
        router.Dispose();
        AssertUsage(router, 0, 0);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers, Is.Empty);
    }

    [Test]
    public void AggregateCountIsSharedByBothProtocolFamilies()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { MaxRemoteSubscriptions = 1 });
        Connect(router, 1);
        Connect(router, 2, protocol: PubsubRouter.FloodsubProtocolVersion);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["a"], []));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["b"], []));
        AssertUsage(router, 1, 2);
        Assert.That(((IRoutingStateContainer)router).FloodsubPeers, Is.Empty);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], ["a"]));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["b"], []));
        AssertUsage(router, 1, 2);
        Assert.That(((IRoutingStateContainer)router).FloodsubPeers["b"], Has.Member(TestPeers.PeerId(2)));
    }

    [TestCase("aaaa")]
    [TestCase("é漢😀")]
    public void ByteBudgetsMeasureUtf16StorageAndRejectAnOversizedFirstItem(string topic)
    {
        topic = string.Concat(Enumerable.Repeat(topic, 1_024));
        PubsubSettings settings = new() { MaxRemoteSubscriptionTopicBytesPerPeer = 2L * topic.Length };
        using PubsubRouter router = new(new PeerStore(), settings);
        Connect(router, 1);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([topic + "x", topic, "b"], []));
        AssertUsage(router, 1, 2L * topic.Length);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], [topic]));
        AssertUsage(router, 0, 0);
    }

    [Test]
    public async Task LateLocalSubscriptionFindsRetainedRemoteAnnouncements()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { UnsubscribeBackoff = 0 });
        Connect(router, 1);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["late"], []));
        IRoutingStateContainer state = router;
        Assert.That(state.Mesh, Is.Empty, "Remote announcements must not establish local interest.");
        ITopic topic = router.GetTopic("late");
        await router.Heartbeat();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(state.Mesh["late"], Has.Member(TestPeers.PeerId(1)));
        topic.Unsubscribe();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        AssertUsage(router, 1, 8);
        topic.Subscribe();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        await router.Heartbeat();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(state.Mesh["late"], Has.Member(TestPeers.PeerId(1)));
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], ["late"]));
        AssertUsage(router, 0, 0);
        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers, Is.Empty);
            Assert.That(state.Mesh["late"], Is.Empty, "Keep the active local mesh available for refilling.");
        });
    }

    [Test]
    public async Task GraftAndPartialMetadataCannotBypassCapacity()
    {
        PubsubSettings settings = new() { MaxRemoteSubscriptionsPerPeer = 1, EnablePartialMessages = true };
        using PubsubRouter router = new(new PeerStore(), settings);
        await router.StartAsync(new LocalPeerStub());
        router.GetPartialMessagesTopic("a", new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true });
        router.GetPartialMessagesTopic("b", new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true });
        List<Rpc> sent = [];
        Connect(router, 1, sent.Add, PubsubRouter.GossipsubProtocolVersionV13);
        router.OnRpc(TestPeers.PeerId(1), new Rpc { Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } } });
        Rpc subscriptions = new();
        subscriptions.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "a", Subscribe = true, RequestsPartial = true });
        subscriptions.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "b", Subscribe = true, RequestsPartial = true });
        router.OnRpc(TestPeers.PeerId(1), subscriptions);
        sent.Clear();
        router.SendPartial(TestPeers.PeerId(1), "b", [1], [2]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent, Is.Empty, "A rejected subscription must not retain partial-message metadata.");
        router.SendPartial(TestPeers.PeerId(1), "a", [1], [2]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent.Select(r => r.Partial).Where(p => p is not null).Count(), Is.EqualTo(1));
        Rpc duplicate = new();
        duplicate.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "a", Subscribe = true, RequestsPartial = false, SupportsSendingPartial = true });
        router.OnRpc(TestPeers.PeerId(1), duplicate);
        AssertUsage(router, 1, 2);
        sent.Clear();
        router.SendPartial(TestPeers.PeerId(1), "a", [1], [2], [3]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent.Single().Partial.HasPartialMessage, Is.False, "Duplicate announcements update partial-message options without a charge.");
        Assert.That(sent.Single().Partial.PartsMetadata.ToByteArray(), Is.EqualTo(new byte[] { 3 }));
        sent.Clear();
        router.OnRpc(TestPeers.PeerId(1), Graft("b"));
        Assert.That(sent.SelectMany(r => r.Control?.Prune ?? []).Select(p => p.TopicID), Has.Member("b"));
        Assert.That(((IRoutingStateContainer)router).Mesh["b"], Is.Empty);
        AssertUsage(router, 1, 2);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], ["a"]));
        sent.Clear();
        router.SendPartial(TestPeers.PeerId(1), "a", [1], [2]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent, Is.Empty, "Unsubscribe releases partial-message metadata.");
        router.OnRpc(TestPeers.PeerId(1), Graft("b"));
        Assert.That(((IRoutingStateContainer)router).Mesh["b"], Has.Member(TestPeers.PeerId(1)));
        AssertUsage(router, 1, 2);
    }

    [TestCase(PubsubRouter.FloodsubProtocolVersion)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11)]
    public void DefaultCountLimitSurvivesMultipleFramesAndReleasesOnDisconnect(string protocol)
    {
        PubsubSettings settings = new();
        using PubsubRouter router = new(new PeerStore(), settings);
        TaskCompletionSource closed = Connect(router, 1, protocol: protocol);
        string[] topics = Enumerable.Range(0, 3_000).Select(i => $"remote-{i}").ToArray();
        foreach (string[] batch in topics.Chunk(1_000))
        {
            Rpc rpc = new Rpc().WithTopics(batch, []);
            Assert.That(rpc.CalculateSize(), Is.LessThanOrEqualTo(settings.MaxRpcBytes));
            router.OnRpc(TestPeers.PeerId(1), rpc);
        }
        AssertUsage(router, settings.MaxRemoteSubscriptionsPerPeer,
            topics.Take(settings.MaxRemoteSubscriptionsPerPeer).Sum(topic => 2L * topic.Length));
        closed.SetResult();
        Assert.That(SpinWait.SpinUntil(() => router.RemoteSubscriptionUsage.Count == 0, TimeSpan.FromSeconds(5)), Is.True);
        AssertUsage(router, 0, 0);
    }

    [Test]
    public async Task JunkAtTheDefaultGlobalLimitCannotExcludeAWantedPeer()
    {
        PubsubSettings settings = new();
        using PubsubRouter router = new(new PeerStore(), settings);
        IRoutingStateContainer state = router;
        router.GetTopic("wanted");
        int junkPeers = settings.MaxRemoteSubscriptions / settings.MaxRemoteSubscriptionsPerPeer;
        for (int index = 1; index <= junkPeers; index++)
        {
            Connect(router, index);
            router.OnRpc(TestPeers.PeerId(index), new Rpc().WithTopics(
                Enumerable.Range(0, settings.MaxRemoteSubscriptionsPerPeer).Select(i => $"junk-{index}-{i}"), []));
        }
        Assert.That(router.RemoteSubscriptionUsage.Count, Is.EqualTo(settings.MaxRemoteSubscriptions));
        int honest = junkPeers + 1;
        List<Rpc> sent = [];
        Connect(router, honest, sent.Add);
        router.OnRpc(TestPeers.PeerId(honest), new Rpc().WithTopics(["wanted"], []));
        bool learned = state.GossipsubPeers.TryGetValue("wanted", out HashSet<PeerId>? members) && members.Contains(TestPeers.PeerId(honest));
        await router.Heartbeat();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        bool joined = state.Mesh["wanted"].Contains(TestPeers.PeerId(honest));
        sent.Clear();
        router.OnRpc(TestPeers.PeerId(honest), Graft("wanted"));
        Assert.Multiple(() =>
        {
            Assert.That(learned, Is.True, "Unwanted announcements must not exclude a wanted membership.");
            Assert.That(joined, Is.True, "Heartbeat must discover the honest peer despite global saturation.");
            Assert.That(state.Mesh["wanted"], Has.Member(TestPeers.PeerId(honest)));
            Assert.That(sent.SelectMany(rpc => rpc.Control?.Prune ?? []), Is.Empty);
            Assert.That(router.RemoteSubscriptionUsage.Count, Is.EqualTo(settings.MaxRemoteSubscriptions));
            Assert.That(router.RemoteSubscriptionUsage.TopicBytes, Is.LessThanOrEqualTo(settings.MaxRemoteSubscriptionTopicBytes));
        });
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    [TestCase(true, false, true)]
    public async Task WantedSubscriptionsReclaimJunkWithinGlobalCountAndByteLimits(bool bytes, bool floodsub, bool direct)
    {
        PubsubSettings settings = new();
        if (bytes) settings.MaxRemoteSubscriptionTopicBytes = 8;
        else settings.MaxRemoteSubscriptions = 2;
        settings.DirectPeers = direct ? [TestPeers.Multiaddr(3)] : [];
        using PubsubRouter router = new(new PeerStore(), settings);
        await router.StartAsync(new LocalPeerStub(), new CancellationToken(true));
        router.GetTopic("want");
        router.GetTopic("aa", subscribe: false);
        settings.TopicScoreParams["bb"] = new TopicScoreParams();
        Connect(router, 1);
        Connect(router, 2, protocol: PubsubRouter.FloodsubProtocolVersion);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["aa"], []));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["bb"], []));
        AssertUsage(router, 2, 8);
        List<Rpc> sent = [];
        Connect(router, 3, sent.Add, floodsub ? PubsubRouter.FloodsubProtocolVersion : PubsubRouter.GossipsubProtocolVersionV11);
        router.OnRpc(TestPeers.PeerId(3), new Rpc().WithTopics(["want"], []));
        AssertUsage(router, bytes ? 1 : 2, bytes ? 8 : 12);
        IRoutingStateContainer state = router;
        var table = floodsub ? state.FloodsubPeers : state.GossipsubPeers;
        Assert.That(table["want"], Has.Member(TestPeers.PeerId(3)));
        await router.Heartbeat();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        if (!floodsub && !direct) Assert.That(state.Mesh["want"], Has.Member(TestPeers.PeerId(3)));
        else Assert.That(state.Mesh["want"], Is.Empty);
        sent.Clear();
        router.Publish("want", [1]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(1), "Reclaimed membership must participate in production forwarding.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void GraftReclaimsUnwantedGlobalCapacity(bool bytes)
    {
        PubsubSettings settings = new();
        if (bytes) settings.MaxRemoteSubscriptionTopicBytes = 8;
        else settings.MaxRemoteSubscriptions = 1;
        using PubsubRouter router = new(new PeerStore(), settings);
        router.Subscribe("want");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Connect(router, 1);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["junk"], []));
        List<Rpc> sent = [];
        Connect(router, 2, sent.Add);
        router.OnRpc(TestPeers.PeerId(2), Graft("want"));
        AssertUsage(router, 1, 8);
        Assert.Multiple(() =>
        {
            Assert.That(((IRoutingStateContainer)router).GossipsubPeers["want"], Has.Member(TestPeers.PeerId(2)));
            Assert.That(((IRoutingStateContainer)router).Mesh["want"], Has.Member(TestPeers.PeerId(2)));
            Assert.That(sent.SelectMany(rpc => rpc.Control?.Prune ?? []), Is.Empty);
        });
    }

    [Test]
    public void LocalSubscriptionChangesProtectAndReleaseRetainedMemberships()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { MaxRemoteSubscriptions = 2 });
        IRoutingStateContainer state = router;
        for (int i = 1; i <= 4; i++) Connect(router, i);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["late"], []));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["junk"], []));
        router.Subscribe("late");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.Subscribe("want");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.OnRpc(TestPeers.PeerId(3), new Rpc().WithTopics(["want"], []));
        Assert.That(state.GossipsubPeers.Keys, Is.EquivalentTo(new[] { "late", "want" }));
        router.Unsubscribe("late");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.Subscribe("next");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.OnRpc(TestPeers.PeerId(4), new Rpc().WithTopics(["next"], []));
        Assert.That(state.GossipsubPeers.Keys, Is.EquivalentTo(new[] { "want", "next" }));
        AssertUsage(router, 2, 16);
        router.UnsubscribeAll();
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.Subscribe("last");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["last"], []));
        AssertUsage(router, 2, 16);
        Assert.That(state.GossipsubPeers["last"], Has.Member(TestPeers.PeerId(1)));
    }

    [Test]
    public void AnImpossibleWantedMembershipDoesNotEvictAnything()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { MaxRemoteSubscriptionTopicBytes = 12 });
        Connect(router, 1);
        Connect(router, 2);
        Connect(router, 3);
        router.Subscribe("saved");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.Subscribe("want");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["saved"], []));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["x"], []));
        router.OnRpc(TestPeers.PeerId(3), new Rpc().WithTopics(["want"], []));
        AssertUsage(router, 2, 12);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers.Keys, Is.EquivalentTo(new[] { "saved", "x" }),
            "Protected state and the entire new membership must fit before any eviction.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SubscribedMembershipsRemainSubjectToGlobalLimits(bool bytes)
    {
        PubsubSettings settings = new();
        if (bytes) settings.MaxRemoteSubscriptionTopicBytes = 2;
        else settings.MaxRemoteSubscriptions = 1;
        using PubsubRouter router = new(new PeerStore(), settings);
        router.Subscribe("a");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.Subscribe("b");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Connect(router, 1);
        List<Rpc> sent = [];
        Connect(router, 2, sent.Add);
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics(["a"], []));
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["b"], []));
        router.OnRpc(TestPeers.PeerId(2), Graft("b"));
        AssertUsage(router, 1, 2);
        Assert.That(((IRoutingStateContainer)router).GossipsubPeers.Keys, Is.EquivalentTo(new[] { "a" }));
        Assert.That(sent.SelectMany(rpc => rpc.Control?.Prune ?? []).Select(p => p.TopicID), Has.Member("b"));
    }

    [Test]
    public async Task EvictionReleasesPartialMetadataAndSurvivesVictimDisconnect()
    {
        using PubsubRouter router = new(new PeerStore(), new PubsubSettings { MaxRemoteSubscriptions = 1, EnablePartialMessages = true });
        await router.StartAsync(new LocalPeerStub(), new CancellationToken(true));
        router.GetPartialMessagesTopic("junk", new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: false);
        router.Subscribe("want");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        List<Rpc> sent = [];
        TaskCompletionSource closed = Connect(router, 1, sent.Add, PubsubRouter.GossipsubProtocolVersionV13);
        router.OnRpc(TestPeers.PeerId(1), new Rpc
        {
            Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } },
            Subscriptions = { new Rpc.Types.SubOpts { Topicid = "junk", Subscribe = true, RequestsPartial = true } }
        });
        router.SendPartial(TestPeers.PeerId(1), "junk", [1], [2]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent.Any(rpc => rpc.Partial is not null), Is.True);
        Connect(router, 2);
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["want"], []));
        sent.Clear();
        router.SendPartial(TestPeers.PeerId(1), "junk", [1], [2]);
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        Assert.That(sent, Is.Empty, "Eviction releases partial-message metadata with the membership.");
        router.OnRpc(TestPeers.PeerId(1), new Rpc().WithTopics([], ["junk"]));
        closed.SetResult();
        Assert.That(SpinWait.SpinUntil(() => !((IRoutingStateContainer)router).ConnectedPeers.Contains(TestPeers.PeerId(1)), TimeSpan.FromSeconds(5)), Is.True);
        lock (router) AssertUsage(router, 1, 8);
        router.Unsubscribe("want");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.Subscribe("next");
        router.FlushOutboundAsync().GetAwaiter().GetResult();
        router.OnRpc(TestPeers.PeerId(2), new Rpc().WithTopics(["next"], []));
        AssertUsage(router, 1, 8);
        router.Dispose();
        AssertUsage(router, 0, 0);
    }

    [TestCase("peer-count", 0)]
    [TestCase("peer-count", -1)]
    [TestCase("peer-bytes", 0)]
    [TestCase("peer-bytes", -1)]
    [TestCase("total-count", 0)]
    [TestCase("total-count", -1)]
    [TestCase("total-bytes", 0)]
    [TestCase("total-bytes", -1)]
    public void ZeroAndNegativeBudgetsAreRejected(string budget, int value)
    {
        PubsubSettings settings = new();
        switch (budget)
        {
            case "peer-count": settings.MaxRemoteSubscriptionsPerPeer = value; break;
            case "peer-bytes": settings.MaxRemoteSubscriptionTopicBytesPerPeer = value; break;
            case "total-count": settings.MaxRemoteSubscriptions = value; break;
            case "total-bytes": settings.MaxRemoteSubscriptionTopicBytes = value; break;
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), settings));
    }

    private static Rpc Graft(string topic) => new() { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = topic } } } };

    private static void AssertUsage(PubsubRouter router, int count, long bytes)
    {
        IRoutingStateContainer state = router;
        Assert.Multiple(() =>
        {
            Assert.That(router.RemoteSubscriptionUsage, Is.EqualTo((count, bytes)));
            Assert.That(state.GossipsubPeers.Values.Concat(state.FloodsubPeers.Values).Sum(p => p.Count), Is.EqualTo(count));
            Assert.That(state.GossipsubPeers.Concat(state.FloodsubPeers).Sum(p => 2L * p.Key.Length * p.Value.Count), Is.EqualTo(bytes));
        });
    }

    private static TaskCompletionSource Connect(PubsubRouter router, int index, Action<Rpc>? send = null,
        string protocol = PubsubRouter.GossipsubProtocolVersionV11)
    {
        TaskCompletionSource closed = new();
        router.OutboundConnection(TestPeers.Multiaddr(index), protocol, closed.Task, send ?? (_ => { }));
        return closed;
    }
}
