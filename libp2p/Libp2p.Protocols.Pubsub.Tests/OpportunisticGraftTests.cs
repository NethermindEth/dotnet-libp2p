// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class OpportunisticGraftTests
{
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record Peer(PubsubRouter.PubsubPeer State, List<Rpc> Sent)
    {
        public PeerId Id => State.PeerId;
        public IEnumerable<ControlGraft> Grafts => Sent.SelectMany(rpc => rpc.Control?.Graft ?? []);
    }

    private static PubsubSettings Settings() => new()
    {
        AutoConnect = false,
        HeartbeatInterval = int.MaxValue,
        DecayInterval = int.MaxValue,
        AppSpecificWeight = 1,
        LowestDegree = 0,
        DScore = 2,
        DOut = 0,
        OpportunisticGraftTicks = 1,
    };

    private static async Task<Peer> Open(PubsubRouter router, int id, double score,
        bool inMesh = false, string? topic = "topic", string protocol = PubsubRouter.GossipsubProtocolVersionV11)
    {
        List<Rpc> sent = [];
        PubsubRouter.PubsubPeer state = router.OutboundConnection(TestPeers.Multiaddr(id), protocol,
            new TaskCompletionSource().Task, (bytes, _) =>
            {
                sent.Add(RouterTestExtensions.Decode(bytes));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
        router.SetAppSpecificScore(state.PeerId, score);
        if (topic is not null)
        {
            Rpc rpc = new Rpc().WithTopics([topic], []);
            if (inMesh) rpc.Control = new ControlMessage { Graft = { new ControlGraft { TopicID = topic } } };
            await router.OnRpcAsync(state.PeerId, rpc);
        }
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        sent.Clear();
        return new(state, sent);
    }

    private static async Task Tick(PubsubRouter router)
    {
        await router.Heartbeat();
        await router.FlushOutboundAsync().WaitAsync(Deadline);
    }

    private static HashSet<PeerId> Mesh(PubsubRouter router) => ((IRoutingStateContainer)router).Mesh["topic"];

    [Test]
    public async Task LowMedianGraftsOnlyConfiguredNumberOfBetterPeersOnce()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        router.GetTopic("topic");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 1, inMesh: true);
        Peer[] better = [await Open(router, 3, 10), await Open(router, 4, 11), await Open(router, 5, 12)];
        Peer lower = await Open(router, 6, 0);
        Peer negative = await Open(router, 7, -1);

        await Tick(router);

        Peer[] grafted = better.Where(peer => Mesh(router).Contains(peer.Id)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(grafted, Has.Length.EqualTo(2));
            Assert.That(Mesh(router), Is.EquivalentTo(new[] { first.Id, second.Id }.Concat(grafted.Select(peer => peer.Id))));
            Assert.That(better.SelectMany(peer => peer.Grafts).Select(graft => graft.TopicID), Is.EqualTo(new[] { "topic", "topic" }));
            foreach (Peer peer in better)
            {
                bool joined = grafted.Contains(peer);
                Assert.That(peer.Grafts.Count(), Is.EqualTo(joined ? 1 : 0));
                Assert.That(peer.State.Score.GetTopicScore("topic").MeshJoinTime.HasValue, Is.EqualTo(joined));
            }
            Assert.That(first.Grafts.Concat(second.Grafts).Concat(lower.Grafts).Concat(negative.Grafts), Is.Empty);
        });

        await Tick(router);
        Assert.That(better.SelectMany(peer => peer.Grafts).Count(), Is.EqualTo(2), "The improved median is now healthy.");
    }

    [TestCase(5)]
    [TestCase(6)]
    public async Task HealthyMedianDoesNotGraft(double score)
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        router.GetTopic("topic");
        Peer first = await Open(router, 1, score, inMesh: true);
        Peer second = await Open(router, 2, score, inMesh: true);
        Peer candidate = await Open(router, 3, 20);

        await Tick(router);

        Assert.That(Mesh(router), Is.EquivalentTo(new[] { first.Id, second.Id }));
        Assert.That(candidate.Grafts, Is.Empty);
        Assert.That(candidate.State.Score.GetTopicScore("topic").MeshJoinTime, Is.Null);
    }

    [TestCase(3, false)]
    [TestCase(4, true)]
    public async Task EvenMeshUsesAveragedMedianAndStrictScoreBoundaries(double threshold, bool graft)
    {
        PubsubSettings settings = Settings();
        settings.OpportunisticGraftThreshold = threshold;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 5, inMesh: true);
        Peer equal = await Open(router, 3, 3);
        Peer lower = await Open(router, 4, 2);
        Peer better = await Open(router, 5, 4);

        await Tick(router);

        Assert.That(Mesh(router), Is.EquivalentTo(graft ? new[] { first.Id, second.Id, better.Id } : new[] { first.Id, second.Id }));
        Assert.That(better.Grafts.Count(), Is.EqualTo(graft ? 1 : 0));
        Assert.That(equal.Grafts.Concat(lower.Grafts), Is.Empty);
    }

    [Test]
    public async Task OddMeshUsesMiddleScore()
    {
        PubsubSettings settings = Settings();
        settings.OpportunisticGraftThreshold = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        await Open(router, 1, 0, inMesh: true);
        await Open(router, 2, 1, inMesh: true);
        await Open(router, 3, 10, inMesh: true);
        Peer candidate = await Open(router, 4, 2);

        await Tick(router);

        Assert.That(candidate.Grafts.Select(graft => graft.TopicID), Is.EqualTo(new[] { "topic" }));
        Assert.That(Mesh(router), Has.Member(candidate.Id));
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task SmallMeshDoesNotGraftOpportunistically(int count)
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        router.GetTopic("topic");
        if (count == 1) await Open(router, 1, 1, inMesh: true);
        Peer candidate = await Open(router, 2, 10);

        await Tick(router);

        Assert.That(Mesh(router), Has.Count.EqualTo(count));
        Assert.That(candidate.Grafts, Is.Empty);
    }

    [Test]
    public async Task OnlySubscribedGossipsubPeersOutsideDirectPeersAndBackoffAreEligible()
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.DirectPeers = [TestPeers.Multiaddr(3)];
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        router.GetTopic("topic");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 1, inMesh: true);
        Peer direct = await Open(router, 3, 10);
        Peer backedOff = await Open(router, 4, 10);
        Peer floodsub = await Open(router, 5, 10, protocol: PubsubRouter.FloodsubProtocolVersion);
        Peer unsubscribed = await Open(router, 6, 10, topic: null);
        Peer otherTopic = await Open(router, 7, 10, topic: "other");
        backedOff.State.Backoff["topic"] = clock.Now.UtcDateTime.AddMinutes(1);

        clock.Now += TimeSpan.FromSeconds(59);
        await Tick(router);
        Assert.That(Mesh(router), Is.EquivalentTo(new[] { first.Id, second.Id }));
        Assert.That(backedOff.Grafts, Is.Empty);

        clock.Now += TimeSpan.FromSeconds(1);
        await Tick(router);
        Assert.That(Mesh(router), Is.EquivalentTo(new[] { first.Id, second.Id, backedOff.Id }));
        Assert.That(backedOff.Grafts.Select(graft => graft.TopicID), Is.EqualTo(new[] { "topic" }));
        Assert.That(direct.Grafts.Concat(floodsub.Grafts).Concat(unsubscribed.Grafts).Concat(otherTopic.Grafts), Is.Empty);
    }

    [TestCase(3)]
    [TestCase(60)]
    public async Task GraftingRunsAtConfiguredHeartbeatCadence(int ticks)
    {
        PubsubSettings settings = Settings();
        settings.OpportunisticGraftTicks = ticks;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 1, inMesh: true);
        Peer candidate = await Open(router, 3, 10);

        for (int i = 1; i < ticks; i++)
        {
            await Tick(router);
            Assert.That(Mesh(router), Is.EquivalentTo(new[] { first.Id, second.Id }), $"Heartbeat {i}");
            Assert.That(candidate.Grafts, Is.Empty);
        }
        await Tick(router);
        Assert.That(candidate.Grafts.Count(), Is.EqualTo(1));
        Assert.That(Mesh(router), Has.Member(candidate.Id));

        Peer next = await Open(router, 4, 20);
        for (int i = 1; i < ticks; i++)
        {
            await Tick(router);
            Assert.That(next.Grafts, Is.Empty);
        }
        await Tick(router);
        Assert.That(next.Grafts.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task TopUpAndOpportunisticGraftingDoNotDuplicateGrafts()
    {
        PubsubSettings settings = Settings();
        settings.Degree = settings.LowestDegree = 4;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        await Open(router, 1, 0, inMesh: true);
        await Open(router, 2, 0, inMesh: true);
        Peer[] candidates = [await Open(router, 3, 1), await Open(router, 4, 1), await Open(router, 5, 1), await Open(router, 6, 1)];

        await Tick(router);

        Assert.That(Mesh(router), Has.Count.EqualTo(6));
        foreach (Peer candidate in candidates)
        {
            Assert.That(candidate.Grafts.Select(graft => graft.TopicID), Is.EqualTo(new[] { "topic" }));
        }
    }

    [Test]
    public async Task GraftsAreIndependentForEachTopic()
    {
        await using PubsubRouter router = new(new PeerStore(), Settings());
        router.GetTopic("topic");
        router.GetTopic("other");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 1, inMesh: true);
        foreach (Peer peer in new[] { first, second })
        {
            await router.OnRpcAsync(peer.Id, new Rpc
            {
                Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "other" } } },
            });
        }
        Peer candidate = await Open(router, 3, 10);
        await router.OnRpcAsync(candidate.Id, new Rpc().WithTopics(["other"], []));

        await Tick(router);

        Assert.That(candidate.Grafts.Select(graft => graft.TopicID), Is.EquivalentTo(new[] { "topic", "other" }));
        foreach (string topic in new[] { "topic", "other" })
        {
            Assert.That(((IRoutingStateContainer)router).Mesh[topic], Is.EquivalentTo(new[] { first.Id, second.Id, candidate.Id }));
            Assert.That(candidate.State.Score.GetTopicScore(topic).MeshJoinTime, Is.Not.Null);
        }
    }

    [Test]
    public async Task CandidateEligibilityUsesTotalScoreIncludingIpColocationPenalty()
    {
        PubsubSettings settings = Settings();
        settings.IPColocationFactorThreshold = 1;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 1, inMesh: true);
        Peer candidate = await Open(router, 3, 10);
        Peer collocated = await Open(router, 4, 10, topic: null);
        candidate.State.Score.IPAddress = collocated.State.Score.IPAddress = "127.0.0.1";
        Assert.That(router.GetPeerScore(candidate.Id), Is.LessThan(0));

        await Tick(router);

        Assert.That(candidate.Grafts, Is.Empty);
        Assert.That(Mesh(router), Is.EquivalentTo(new[] { first.Id, second.Id }));
    }

    [Test]
    public async Task FullLowScoringMeshCanImproveAndIsTrimmedOnNextHeartbeat()
    {
        PubsubSettings settings = Settings();
        settings.Degree = settings.HighestDegree = 2;
        settings.OpportunisticGraftTicks = 2;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        Peer first = await Open(router, 1, 1, inMesh: true);
        Peer second = await Open(router, 2, 1, inMesh: true);
        Peer third = await Open(router, 3, 10);
        Peer fourth = await Open(router, 4, 10);

        await Tick(router);
        Assert.That(Mesh(router), Has.Count.EqualTo(2));
        await Tick(router);
        Assert.That(Mesh(router), Has.Count.EqualTo(4));
        await Tick(router);

        Assert.That(Mesh(router), Is.EquivalentTo(new[] { third.Id, fourth.Id }));
        Assert.That(first.State.Score.GetTopicScore("topic").MeshJoinTime, Is.Null);
        Assert.That(second.State.Score.GetTopicScore("topic").MeshJoinTime, Is.Null);
        Assert.That(first.Sent.Concat(second.Sent).SelectMany(rpc => rpc.Control?.Prune ?? []).Count(), Is.EqualTo(2));
    }

    [Test]
    public async Task ZeroPeersDisablesOpportunisticGrafting()
    {
        PubsubSettings settings = Settings();
        settings.OpportunisticGraftPeers = 0;
        await using PubsubRouter router = new(new PeerStore(), settings);
        router.GetTopic("topic");
        await Open(router, 1, 1, inMesh: true);
        await Open(router, 2, 1, inMesh: true);
        Peer candidate = await Open(router, 3, 10);

        await Tick(router);

        Assert.That(Mesh(router), Has.Count.EqualTo(2));
        Assert.That(candidate.Grafts, Is.Empty);
    }

    [TestCase(0, 2)]
    [TestCase(-1, 2)]
    [TestCase(60, -1)]
    public void InvalidSettingsAreRejected(int ticks, int peers)
    {
        PubsubSettings settings = Settings();
        settings.OpportunisticGraftTicks = ticks;
        settings.OpportunisticGraftPeers = peers;
        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubRouter(new PeerStore(), settings));
    }

    [Test]
    public void DefaultsCheckEverySixtyHeartbeatsAndGraftTwoPeers()
    {
        PubsubSettings settings = new();
        Assert.That(settings.OpportunisticGraftTicks, Is.EqualTo(60));
        Assert.That(settings.OpportunisticGraftPeers, Is.EqualTo(2));
    }
}
