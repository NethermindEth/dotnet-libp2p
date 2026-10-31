// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class MeshBackoffTests
{
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PubsubSettings Settings() => new()
    {
        AutoConnect = false,
        HeartbeatInterval = int.MaxValue,
        DecayInterval = int.MaxValue,
        DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        GetMessageId = message => new MessageId(message.Data.ToByteArray()),
        AppSpecificWeight = 1,
    };

    private static PubsubRouter.PubsubPeer Open(PubsubRouter router, int id, List<Rpc> sent, string protocol = PubsubRouter.GossipsubProtocolVersionV11) =>
        router.OutboundConnection(TestPeers.Multiaddr(id), protocol, new TaskCompletionSource().Task,
            (bytes, _) =>
            {
                sent.Add(RouterTestExtensions.Decode(bytes));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => Task.CompletedTask)!;

    private static Rpc Graft(string topic = "topic") => new() { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = topic } } } };
    private static async Task Receive(PubsubRouter router, PeerId peer, Rpc rpc)
    {
        await router.OnRpcAsync(peer, Rpc.Parser.ParseFrom(rpc.ToByteArray()));
        await router.FlushOutboundAsync().WaitAsync(Deadline);
    }

    [TestCase(null)]
    [TestCase(0UL)]
    [TestCase(120UL)]
    [TestCase(86400UL)]
    [TestCase(86401UL)]
    [TestCase(200000000000UL)]
    [TestCase(1000000000000UL)]
    [TestCase(ulong.MaxValue)]
    public async Task ReceivedPruneBoundsBackoffAndContinuesWithLaterPruneAndIhave(ulong? seconds)
    {
        Clock clock = new();
        ILogger logger = Substitute.For<ILogger>();
        ILoggerFactory factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        await using PubsubRouter router = new(new PeerStore(), Settings(), factory, clock);
        router.GetTopic("topic");
        router.GetTopic("second");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, Graft());
        await Receive(router, peer.PeerId, Graft("second"));
        Assert.That(peer.Score.GetTopicScore("topic").MeshJoinTime, Is.Not.Null);
        sent.Clear();
        ControlPrune prune = new() { TopicID = "topic" };
        if (seconds.HasValue) prune.Backoff = seconds.Value;
        await Receive(router, peer.PeerId, new Rpc
        {
            Control = new ControlMessage
            {
                Prune = { prune, new ControlPrune { TopicID = "second", Backoff = 30 } },
                Ihave = { new ControlIHave { TopicID = "topic", MessageIDs = { ByteString.CopyFrom([9]) } } },
            },
        });
        var state = (IRoutingStateContainer)router;
        Assert.Multiple(() =>
        {
            Assert.That(state.Mesh["topic"], Does.Not.Contain(peer.PeerId));
            Assert.That(state.Mesh["second"], Does.Not.Contain(peer.PeerId));
            Assert.That(peer.Score.GetTopicScore("topic").MeshJoinTime, Is.Null);
            Assert.That(peer.Score.GetTopicScore("second").MeshJoinTime, Is.Null);
            Assert.That(peer.Backoff.GetValueOrDefault("topic"), Is.EqualTo(clock.Now.UtcDateTime.AddSeconds(seconds.GetValueOrDefault() == 0 ? 60 : Math.Min(seconds!.Value, 86400))));
            Assert.That(peer.Backoff.GetValueOrDefault("second"), Is.EqualTo(clock.Now.UtcDateTime.AddSeconds(30)));
            Assert.That(sent.SelectMany(rpc => rpc.Control?.Iwant ?? []).SelectMany(want => want.MessageIDs), Is.EqualTo(new[] { ByteString.CopyFrom([9]) }));
            Assert.That(logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == "Log" && call.GetArguments()[0] is LogLevel.Error), Is.False);
        });
    }

    [TestCase(PubsubRouter.GossipsubProtocolVersionV10, -10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11, -10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12, -10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13, -10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV10, 0)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV10, 10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11, 0)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11, 10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12, 0)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12, 10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13, 0)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13, 10)]
    public async Task GraftUsesBaselineScoreAndDoesNotForwardToRejectedPeer(string protocol, double score)
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> candidateSent = [], admittedSent = [], sourceSent = [];
        var candidate = Open(router, 1, candidateSent, protocol);
        var admitted = Open(router, 2, admittedSent);
        var source = Open(router, 3, sourceSent);
        router.SetAppSpecificScore(candidate.PeerId, score);
        Assert.That(router.GetPeerScore(candidate.PeerId), Is.EqualTo(score));
        await Receive(router, admitted.PeerId, Graft());
        candidateSent.Clear();
        await Receive(router, candidate.PeerId, Graft());
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"].Contains(candidate.PeerId), Is.EqualTo(score >= 0));
        if (score < 0)
        {
            ControlPrune prune = candidateSent.SelectMany(rpc => rpc.Control?.Prune ?? []).Single();
            Assert.Multiple(() =>
            {
                Assert.That(prune.HasBackoff, Is.EqualTo(protocol != PubsubRouter.GossipsubProtocolVersionV10));
                Assert.That(prune.Backoff, Is.EqualTo(protocol == PubsubRouter.GossipsubProtocolVersionV10 ? 0 : 60));
                Assert.That(prune.Peers, Is.Empty);
                Assert.That(candidate.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
                Assert.That(candidate.RemoteSubscriptions, Is.Empty, "Rejection precedes implicit membership admission.");
            });
        }
        candidateSent.Clear();
        admittedSent.Clear();
        await Receive(router, source.PeerId, new Rpc { Publish = { new Message { Topic = "topic", Data = ByteString.CopyFrom([1, 2, 3]) } } });
        Assert.That(admittedSent.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(1));
        Assert.That(candidateSent.SelectMany(rpc => rpc.Publish).Count(), Is.EqualTo(score >= 0 ? 1 : 0));
    }

    [Test]
    public async Task PrematureGraftRefreshesBackoffAndRecoveryWaitsUntilExpiry()
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        router.SetAppSpecificScore(peer.PeerId, -10);
        await Receive(router, peer.PeerId, Graft());
        for (int cycle = 0; cycle < 3; cycle++)
        {
            clock.Now += TimeSpan.FromSeconds(10);
            await Receive(router, peer.PeerId, Graft());
            Assert.That(peer.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
            Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Does.Not.Contain(peer.PeerId));
        }
        Assert.That(peer.Score.BehaviorPenalty, Is.EqualTo(3));
        Assert.That(sent.SelectMany(rpc => rpc.Control?.Prune ?? []).All(prune => prune.Peers.Count == 0), Is.True);
        router.SetAppSpecificScore(peer.PeerId, 1000);
        Assert.That(router.GetPeerScore(peer.PeerId), Is.GreaterThan(0));
        await Receive(router, peer.PeerId, new Rpc().WithTopics(["topic"], []));
        DateTime expires = peer.Backoff["topic"];
        clock.Now = new DateTimeOffset(expires.AddTicks(-1));
        await router.Heartbeat();
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Does.Not.Contain(peer.PeerId));
        clock.Now = new DateTimeOffset(expires);
        await Receive(router, peer.PeerId, Graft());
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Has.Member(peer.PeerId));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HeartbeatPruningCreatesBackoff(bool excessDegree)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.DScore = 0;
        settings.DOut = 0;
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent, PubsubRouter.GossipsubProtocolVersionV10);
        await Receive(router, peer.PeerId, Graft());
        if (excessDegree) settings.HighestDegree = settings.LowestDegree = 0;
        else router.SetAppSpecificScore(peer.PeerId, -10);
        sent.Clear();
        await router.Heartbeat();
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Is.Empty);
        Assert.That(peer.Backoff.GetValueOrDefault("topic"), Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
        Assert.That(peer.Score.GetTopicScore("topic").MeshJoinTime, Is.Null);
        Assert.That(sent.SelectMany(rpc => rpc.Control?.Prune ?? []).Single().HasBackoff, Is.False);
        settings.HighestDegree = 12;
        router.SetAppSpecificScore(peer.PeerId, 10);
        clock.Now += TimeSpan.FromSeconds(59);
        await router.Heartbeat();
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Is.Empty);
        clock.Now += TimeSpan.FromSeconds(1);
        await Receive(router, peer.PeerId, Graft());
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Has.Member(peer.PeerId));
    }

    [TestCase(PubsubRouter.GossipsubProtocolVersionV10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13)]
    public async Task HeartbeatExcessDegreePruningPreservesVersionSpecificPx(string protocol)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.Degree = 1;
        settings.LowestDegree = 0;
        settings.HighestDegree = 2;
        settings.DScore = 1;
        settings.DOut = 0;
        PeerStore store = new();
        ByteString retainedRecord = Core.Dto.SigningHelper.CreateSignedEnvelope(TestPeers.Identity(2), [TestPeers.Multiaddr(2)], 1);
        store.Discover(retainedRecord);
        await using PubsubRouter router = new(store, settings, timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> prunedSent = [], retainedSent = [];
        var pruned = Open(router, 1, prunedSent, protocol);
        var retained = Open(router, 2, retainedSent);
        await Receive(router, pruned.PeerId, Graft());
        await Receive(router, retained.PeerId, Graft());
        router.SetAppSpecificScore(retained.PeerId, 10);
        var state = (IRoutingStateContainer)router;
        Assert.That(state.Mesh["topic"], Is.EquivalentTo(new[] { pruned.PeerId, retained.PeerId }));
        settings.HighestDegree = 1;
        prunedSent.Clear();
        retainedSent.Clear();

        await router.Heartbeat();
        await router.FlushOutboundAsync().WaitAsync(Deadline);

        Assert.That(state.Mesh["topic"], Is.EquivalentTo(new[] { retained.PeerId }));
        Assert.That(pruned.Score.GetTopicScore("topic").MeshJoinTime, Is.Null);
        Assert.That(pruned.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
        Assert.That(retainedSent.SelectMany(rpc => rpc.Control?.Prune ?? []), Is.Empty);
        ControlPrune prune = prunedSent.SelectMany(rpc => rpc.Control?.Prune ?? []).Single();
        Assert.That(prune.TopicID, Is.EqualTo("topic"));
        bool supportsPx = protocol != PubsubRouter.GossipsubProtocolVersionV10;
        Assert.That(prune.HasBackoff, Is.EqualTo(supportsPx));
        Assert.That(prune.Backoff, Is.EqualTo(supportsPx ? 60UL : 0UL));
        if (supportsPx)
        {
            Assert.That(prune.Peers.Select(peer => peer.PeerID), Is.EqualTo(new[] { ByteString.CopyFrom(retained.PeerId.Bytes) }));
            Assert.That(prune.Peers.Single().SignedPeerRecord, Is.EqualTo(retainedRecord));
        }
        else
        {
            Assert.That(prune.Peers, Is.Empty);
        }
    }

    [TestCase(0)]
    [TestCase(1500)]
    [TestCase(-1)]
    [TestCase(172800000)]
    public async Task UnsubscribeUsesLocalDurationAndUtcClock(int milliseconds)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.UnsubscribeBackoff = milliseconds;
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, Graft());
        sent.Clear();
        router.Unsubscribe("topic");
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(peer.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMilliseconds(Math.Max(0, milliseconds))));
        Assert.That(sent.SelectMany(rpc => rpc.Control?.Prune ?? []).Single().Backoff, Is.EqualTo((ulong)Math.Ceiling(Math.Max(0, milliseconds) / 1000.0)));
        router.Subscribe("topic");
        await router.Heartbeat();
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"].Contains(peer.PeerId), Is.EqualTo(milliseconds <= 0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DeadlinesPreserveLongerValidBackoffAndBoundExistingValues(bool invalidExisting)
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, Graft());
        peer.Backoff["topic"] = invalidExisting ? DateTime.MaxValue : clock.Now.UtcDateTime.AddHours(2);
        await Receive(router, peer.PeerId, new Rpc { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = "topic", Backoff = 30 } } } });
        Assert.That(peer.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddHours(invalidExisting ? 24 : 2)));
    }

    [TestCase(PubsubRouter.GossipsubProtocolVersionV10)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV11)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV12)]
    [TestCase(PubsubRouter.GossipsubProtocolVersionV13)]
    public async Task CapacityRejectionRecordsBackoffAndPreservesVersionSpecificPx(string protocol)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.HighestDegree = 2;
        settings.AcceptPXThreshold = 0;
        PeerStore store = new();
        ByteString goodRecord = Core.Dto.SigningHelper.CreateSignedEnvelope(TestPeers.Identity(2), [TestPeers.Multiaddr(2)], 1);
        store.Discover(goodRecord);
        store.Discover(Core.Dto.SigningHelper.CreateSignedEnvelope(TestPeers.Identity(3), [TestPeers.Multiaddr(3)], 1));
        await using PubsubRouter router = new(store, settings, timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [], goodSent = [], badSent = [];
        var candidate = Open(router, 1, sent, protocol);
        var good = Open(router, 2, goodSent);
        var bad = Open(router, 3, badSent);
        await Receive(router, good.PeerId, Graft());
        await Receive(router, bad.PeerId, Graft());
        router.SetAppSpecificScore(bad.PeerId, -10);
        sent.Clear();
        await Receive(router, candidate.PeerId, Graft());
        ControlPrune prune = sent.SelectMany(rpc => rpc.Control?.Prune ?? []).Single();
        Assert.That(candidate.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
        Assert.That(prune.HasBackoff, Is.EqualTo(protocol != PubsubRouter.GossipsubProtocolVersionV10));
        if (protocol == PubsubRouter.GossipsubProtocolVersionV10) Assert.That(prune.Peers, Is.Empty);
        else
        {
            Assert.That(prune.Peers.Select(peer => peer.PeerID), Is.EqualTo(new[] { ByteString.CopyFrom(good.PeerId.Bytes) }));
            Assert.That(prune.Peers[0].SignedPeerRecord, Is.EqualTo(goodRecord));
        }
    }

    [Test]
    public async Task NegativeDuplicateGraftRemovesExistingMembershipAndRecordsDepartureOnce()
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.TopicScoreParams["topic"] = new TopicScoreParams
        {
            TimeInMeshWeight = 0,
            MeshMessageDeliveriesWeight = 0,
            MeshFailurePenaltyWeight = 0,
            MeshMessageDeliveriesActivation = 0,
            MeshMessageDeliveriesThreshold = 2,
        };
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, Graft());
        TopicScore topicScore = peer.Score.GetTopicScore("topic");
        topicScore.MeshJoinTime = DateTime.UtcNow.AddSeconds(-2);
        router.SetAppSpecificScore(peer.PeerId, -10);
        await Receive(router, peer.PeerId, Graft());
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Is.Empty);
        Assert.That(topicScore.MeshJoinTime, Is.Null);
        Assert.That(topicScore.MeshFailurePenalty, Is.EqualTo(4));
        clock.Now += TimeSpan.FromSeconds(1);
        await Receive(router, peer.PeerId, Graft());
        Assert.That(topicScore.MeshFailurePenalty, Is.EqualTo(4));
        Assert.That(peer.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMinutes(1)));
    }

    [TestCase(0)]
    [TestCase(1500)]
    [TestCase(-1)]
    [TestCase(172800000)]
    public async Task MissingWireBackoffUsesConfiguredLocalDuration(int milliseconds)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.PruneBackoff = milliseconds;
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, Graft());
        await Receive(router, peer.PeerId, new Rpc { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = "topic" } } } });
        Assert.That(peer.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime.AddMilliseconds(Math.Max(0, milliseconds))));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SubscribePromotesFanoutOnlyAfterBackoffExpires(bool expired)
    {
        Clock clock = new();
        PubsubSettings settings = Settings();
        settings.FloodPublish = false;
        await using PubsubRouter router = new(new PeerStore(), settings, timeProvider: clock);
        await router.StartAsync(new LocalPeerStub(), new CancellationToken(true));
        router.GetTopic("topic", subscribe: false);
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, new Rpc().WithTopics(["topic"], []));
        router.Publish("topic", [1]);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(((IRoutingStateContainer)router).Fanout["topic"], Has.Member(peer.PeerId));
        peer.Backoff["topic"] = clock.Now.UtcDateTime.AddMinutes(1);
        clock.Now += expired ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(59);
        router.Subscribe("topic");
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"].Contains(peer.PeerId), Is.EqualTo(expired));
    }

    [Test]
    public async Task RemoteAndLocalDeadlinesSaturateAtMaximumDate()
    {
        Clock clock = new();
        await using PubsubRouter router = new(new PeerStore(), Settings(), timeProvider: clock);
        router.GetTopic("topic");
        List<Rpc> sent = [];
        var peer = Open(router, 1, sent);
        await Receive(router, peer.PeerId, Graft());
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Has.Member(peer.PeerId));
        clock.Now = DateTimeOffset.MaxValue.AddSeconds(-1);
        await Receive(router, peer.PeerId, new Rpc { Control = new ControlMessage { Prune = { new ControlPrune { TopicID = "topic", Backoff = ulong.MaxValue } } } });
        Assert.That(((IRoutingStateContainer)router).Mesh["topic"], Is.Empty);
        Assert.That(peer.Backoff.GetValueOrDefault("topic"), Is.EqualTo(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
        clock.Now = DateTimeOffset.MaxValue;
        router.SetAppSpecificScore(peer.PeerId, -10);
        await Receive(router, peer.PeerId, Graft());
        Assert.That(peer.Backoff["topic"], Is.EqualTo(clock.Now.UtcDateTime));
    }
}
