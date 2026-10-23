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
        Assert.That(state.Mesh["late"], Has.Member(TestPeers.PeerId(1)));
        topic.Unsubscribe();
        AssertUsage(router, 1, 8);
        topic.Subscribe();
        await router.Heartbeat();
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
        Assert.That(sent, Is.Empty, "A rejected subscription must not retain partial-message metadata.");
        router.SendPartial(TestPeers.PeerId(1), "a", [1], [2]);
        Assert.That(sent.Select(r => r.Partial).Where(p => p is not null).Count(), Is.EqualTo(1));
        Rpc duplicate = new();
        duplicate.Subscriptions.Add(new Rpc.Types.SubOpts { Topicid = "a", Subscribe = true, RequestsPartial = false, SupportsSendingPartial = true });
        router.OnRpc(TestPeers.PeerId(1), duplicate);
        AssertUsage(router, 1, 2);
        sent.Clear();
        router.SendPartial(TestPeers.PeerId(1), "a", [1], [2], [3]);
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
