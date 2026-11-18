// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(10_000)]
public class PeerSelectionTests
{
    private const string Topic = "topic";
    private static CancellationToken Deadline => TestContext.CurrentContext.CancellationToken;

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }

    private sealed class ScriptedRandom(int offset, params int[] initialOffsets) : Random
    {
        public List<(int Min, int Max)> Calls { get; } = [];

        public override int Next(int minValue, int maxValue)
        {
            Assert.That(maxValue, Is.GreaterThan(minValue));
            int currentOffset = Calls.Count < initialOffsets.Length ? initialOffsets[Calls.Count] : offset;
            Calls.Add((minValue, maxValue));
            return minValue + currentOffset % (maxValue - minValue);
        }
    }

    private static PubsubSettings Settings() => new()
    {
        AutoConnect = false,
        HeartbeatInterval = int.MaxValue,
        DecayInterval = int.MaxValue,
        DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
        GetMessageId = message => new MessageId(message.Data.ToByteArray()),
        AppSpecificWeight = 1,
        FloodPublish = false,
        Degree = 3,
        LowestDegree = 2,
        LazyDegree = 0,
        GossipFactor = 0,
        DirectPeers = [TestPeers.Multiaddr(1)],
    };

    private static async Task Start(PubsubRouter router)
    {
        ILocalPeer local = Substitute.For<ILocalPeer>();
        local.Identity.Returns(TestPeers.Identity(0));
        local.ListenAddresses.Returns(new ObservableCollection<Multiaddress> { TestPeers.Multiaddr(0) });
        await router.StartAsync(local, Deadline);
    }

    private static async Task<PubsubRouter.PubsubPeer> Open(PubsubRouter router, int id,
        ConcurrentQueue<Rpc>? sent = null, bool partial = false, bool supportsSendingPartial = false)
    {
        PubsubRouter.PubsubPeer peer = router.OutboundConnection(TestPeers.Multiaddr(id),
            partial || supportsSendingPartial ? PubsubRouter.GossipsubProtocolVersionV13 : PubsubRouter.GossipsubProtocolVersionV11,
            new TaskCompletionSource().Task, (bytes, _) =>
            {
                sent?.Enqueue(RouterTestExtensions.Decode(bytes));
                return ValueTask.FromResult(IOResult.Ok);
            }, () => ValueTask.CompletedTask, () => Task.CompletedTask)!;
        Rpc subscription = new Rpc().WithTopics([Topic], []);
        if (partial || supportsSendingPartial)
        {
            subscription.Subscriptions[0].RequestsPartial = partial;
            subscription.Subscriptions[0].SupportsSendingPartial = supportsSendingPartial;
            subscription.Control = new ControlMessage { Extensions = new ControlExtensions { PartialMessages = true } };
        }
        await router.OnRpcAsync(peer.PeerId, subscription);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        return peer;
    }

    private static async Task Graft(PubsubRouter router, PeerId peer)
    {
        await router.OnRpcAsync(peer, new Rpc
        {
            Control = new ControlMessage { Graft = { new ControlGraft { TopicID = Topic } } },
        });
        await router.FlushOutboundAsync().WaitAsync(Deadline);
    }

    private static PeerId[] Eligible(int count = 6) => Enumerable.Range(10, count).Select(TestPeers.PeerId).ToArray();

    private static void AssertSample(IEnumerable<PeerId> selected, PeerId[] eligible, int count, ScriptedRandom random)
    {
        PeerId[] peers = selected.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(peers, Has.Length.EqualTo(count));
            Assert.That(peers, Is.Unique);
            Assert.That(peers, Is.SubsetOf(eligible));
            Assert.That(random.Calls, Is.EqualTo(Enumerable.Range(0, Math.Min(count, Math.Max(0, eligible.Length - 1))).Select(i => (i, eligible.Length))),
                "Filter before sampling and use one bounded draw per non-forced choice.");
        });
    }

    private static async Task<HashSet<PeerId>> OpenOversizedMesh(PubsubRouter router, int peerCount, int? soleOutboundId = null)
    {
        router.Subscribe(Topic);
        HashSet<PeerId> mesh = ((IRoutingStateContainer)router).Mesh[Topic];
        // Seed an oversized mesh directly; GRAFT admission caps it at HighestDegree.
        foreach (int id in Enumerable.Range(10, peerCount))
        {
            if (soleOutboundId.HasValue && id != soleOutboundId)
            {
                router.InboundConnection(TestPeers.Multiaddr(id), PubsubRouter.GossipsubProtocolVersionV11,
                    new TaskCompletionSource().Task, () => Task.CompletedTask);
            }
            var peer = await Open(router, id);
            mesh.Add(peer.PeerId);
            peer.Score.GetTopicScore(Topic).JoinMesh();
        }
        return mesh;
    }

    [TestCase(-1, 0)]
    [TestCase(0, 0)]
    [TestCase(2, 2)]
    [TestCase(4, 4)]
    [TestCase(10, 4)]
    public void Sample_ClampsCountAndUsesBoundedDraws(int requested, int expected)
    {
        int[] candidates = [0, 1, 2, 3];
        ScriptedRandom random = new(0);

        int[] selected = PubsubRouter.Sample(candidates, requested, random);

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.EqualTo(Enumerable.Range(0, expected)));
            Assert.That(random.Calls, Is.EqualTo(Enumerable.Range(0, Math.Min(expected, 3)).Select(i => (i, 4))));
        });
    }

    [TestCase(3)]
    [TestCase(4)]
    public void Sample_DoesNotMutateSourceWhenPermuting(int count)
    {
        int[] candidates = [0, 1, 2, 3];

        int[] selected = PubsubRouter.Sample(candidates, count, new ScriptedRandom(11));

        Assert.Multiple(() =>
        {
            Assert.That(selected, Is.EqualTo(new[] { 3, 0, 1, 2 }.Take(count)));
            Assert.That(candidates, Is.EqualTo(new[] { 0, 1, 2, 3 }), "Sampling must not mutate the source.");
        });
    }

    [Test]
    public void Sample_AllDrawSequencesGiveEveryOrderedPairOnce()
    {
        // There are 3 * 2 equally likely draw sequences when taking two of three candidates.
        List<int[]> samples = [];
        for (int offset = 0; offset < 6; offset++)
        {
            samples.Add(PubsubRouter.Sample(new[] { 0, 1, 2 }, 2, new ScriptedRandom(offset)));
        }

        Assert.That(samples.Select(pair => (pair[0], pair[1])), Is.EquivalentTo(new[]
        {
            (0, 1), (0, 2), (1, 0), (1, 2), (2, 0), (2, 1),
        }));
        Assert.That(PubsubRouter.Sample(Array.Empty<int>(), 3, new ScriptedRandom(0)), Is.Empty);
        Assert.That(PubsubRouter.Sample(new[] { 0, 1, 2, 3 }, 3, new ScriptedRandom(11)), Is.EqualTo(new[] { 3, 0, 1 }));
    }

    [Test]
    public async Task HeartbeatMeshGrowth_SamplesOnlyEligiblePeers()
    {
        PeerId[] eligible = Eligible();
        HashSet<PeerId> reached = [];
        for (int offset = 0; offset < eligible.Length; offset++)
        {
            ScriptedRandom random = new(offset);
            Clock clock = new();
            await using PubsubRouter router = new(new PeerStore(), Settings(), null, clock, random);
            router.Subscribe(Topic);
            await Open(router, 1); // Direct peer.
            var negative = await Open(router, 2);
            router.SetAppSpecificScore(negative.PeerId, -1);
            var backedOff = await Open(router, 3);
            backedOff.Backoff[Topic] = clock.GetUtcNow().UtcDateTime.AddMinutes(1);
            var existing = await Open(router, 4);
            await Graft(router, existing.PeerId);
            foreach (int id in Enumerable.Range(10, eligible.Length)) await Open(router, id);

            await router.Heartbeat();

            HashSet<PeerId> mesh = ((IRoutingStateContainer)router).Mesh[Topic];
            Assert.That(mesh, Has.Count.EqualTo(3));
            Assert.That(mesh, Does.Contain(existing.PeerId));
            PeerId[] selected = mesh.Except([existing.PeerId]).ToArray();
            AssertSample(selected, eligible, 2, random);
            reached.UnionWith(selected);
        }
        Assert.That(reached, Is.EquivalentTo(eligible), "Every eligible peer must be reachable with the same insertion order.");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PublishFanout_SamplesOnlyEligiblePeers(bool partial)
    {
        PeerId[] eligible = Eligible();
        HashSet<PeerId> reached = [];
        for (int offset = 0; offset < eligible.Length; offset++)
        {
            ScriptedRandom random = new(offset);
            PubsubSettings settings = Settings();
            settings.EnablePartialMessages = partial;
            await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), random);
            await Start(router);
            if (partial)
            {
                router.GetPartialMessagesTopic(Topic, new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: false);
            }
            await Open(router, 1, partial: partial);
            var negative = await Open(router, 2, partial: partial);
            router.SetAppSpecificScore(negative.PeerId, -1);
            ConcurrentQueue<Rpc> sent = [];
            foreach (int id in Enumerable.Range(10, eligible.Length)) await Open(router, id, sent, partial);
            sent.Clear();

            if (partial) router.PublishPartial(Topic, [1], partialMessage: [2]);
            else router.Publish(Topic, [2]);
            await router.FlushOutboundAsync().WaitAsync(Deadline);

            HashSet<PeerId> selected = ((IRoutingStateContainer)router).Fanout[Topic];
            AssertSample(selected, eligible, settings.Degree, random);
            Assert.That(sent.Count(rpc => partial ? rpc.Partial is not null : rpc.Publish.Count != 0), Is.EqualTo(settings.Degree));
            reached.UnionWith(selected);
        }
        Assert.That(reached, Is.EquivalentTo(eligible));
    }

    [Test]
    public async Task HeartbeatFanoutTopUp_SamplesMissingPeers()
    {
        PeerId[] eligible = Eligible();
        HashSet<PeerId> reached = [];
        for (int offset = 0; offset < eligible.Length; offset++)
        {
            ScriptedRandom random = new(offset);
            await using PubsubRouter router = new(new PeerStore(), Settings(), null, new Clock(), random);
            await Start(router);
            var existing = await Open(router, 4);
            router.Publish(Topic, [1]);
            Assert.That(((IRoutingStateContainer)router).Fanout[Topic], Is.EquivalentTo(new[] { existing.PeerId }));
            random.Calls.Clear();
            await Open(router, 1);
            foreach (int id in Enumerable.Range(10, eligible.Length)) await Open(router, id);

            await router.Heartbeat();

            HashSet<PeerId> fanout = ((IRoutingStateContainer)router).Fanout[Topic];
            Assert.That(fanout, Has.Count.EqualTo(3));
            Assert.That(fanout, Does.Contain(existing.PeerId));
            PeerId[] selected = fanout.Except([existing.PeerId]).ToArray();
            AssertSample(selected, eligible, 2, random);
            reached.UnionWith(selected);
        }
        Assert.That(reached, Is.EquivalentTo(eligible));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HeartbeatFanout_ExcludesPeersBelowPublishThreshold(bool alreadyInFanout)
    {
        PubsubSettings settings = Settings();
        settings.Degree = 1;
        settings.LowestDegree = 1;
        await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), new ScriptedRandom(1));
        await Start(router);
        ConcurrentQueue<Rpc> lowScoreRpcs = [];
        ConcurrentQueue<Rpc> eligibleRpcs = [];
        IRoutingStateContainer state = router;

        if (alreadyInFanout) await Open(router, 3, lowScoreRpcs);
        router.Publish(Topic, [1]);
        await router.FlushOutboundAsync().WaitAsync(Deadline);
        Assert.That(state.Fanout[Topic], Is.EquivalentTo(alreadyInFanout ? new[] { TestPeers.PeerId(3) } : []));

        var eligible = await Open(router, 2, eligibleRpcs);
        router.SetAppSpecificScore(eligible.PeerId, settings.PublishThreshold);
        if (!alreadyInFanout) await Open(router, 3, lowScoreRpcs);
        router.SetAppSpecificScore(TestPeers.PeerId(3), settings.PublishThreshold - 10);
        lowScoreRpcs.Clear();

        await router.Heartbeat();
        router.Publish(Topic, [2]);
        await router.FlushOutboundAsync().WaitAsync(Deadline);

        Assert.Multiple(() =>
        {
            Assert.That(state.Fanout[Topic], Is.EquivalentTo(new[] { eligible.PeerId }));
            Assert.That(eligibleRpcs.SelectMany(rpc => rpc.Publish).Select(message => message.Data), Is.EqualTo(new[] { ByteString.CopyFrom([2]) }));
            Assert.That(lowScoreRpcs.SelectMany(rpc => rpc.Publish), Is.Empty);
        });
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task PublishPartialFanout_SamplesPeersThatAcceptTheSuppliedField(bool sharedFanout, bool metadataOnly)
    {
        PubsubSettings settings = Settings();
        settings.EnablePartialMessages = true;
        settings.Degree = 1;
        settings.LowestDegree = 1;
        await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), new ScriptedRandom(1));
        await Start(router);
        router.GetPartialMessagesTopic(Topic, new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: false);
        ConcurrentQueue<Rpc> eligibleRpcs = [];
        ConcurrentQueue<Rpc> incompatibleRpcs = [];
        var eligible = await Open(router, 2, eligibleRpcs, partial: !metadataOnly, supportsSendingPartial: metadataOnly);
        var legacy = await Open(router, 3, incompatibleRpcs);
        // Can receive metadata but must not occupy the data-only slot.
        if (!metadataOnly) await Open(router, 4, incompatibleRpcs, supportsSendingPartial: true);
        IRoutingStateContainer state = router;
        if (sharedFanout)
        {
            // Seed the shared fanout through ordinary publishing with only the legacy peer eligible.
            router.SetAppSpecificScore(eligible.PeerId, settings.PublishThreshold - 10);
            if (!metadataOnly) router.SetAppSpecificScore(TestPeers.PeerId(4), settings.PublishThreshold - 10);
            router.Publish(Topic, [1]);
            router.SetAppSpecificScore(eligible.PeerId, 0);
            if (!metadataOnly) router.SetAppSpecificScore(TestPeers.PeerId(4), 0);
            Assert.That(state.Fanout[Topic], Is.EquivalentTo(new[] { legacy.PeerId }));
        }

        router.PublishPartial(Topic, [1], partialMessage: metadataOnly ? null : [2], partsMetadata: metadataOnly ? [3] : null);
        await router.FlushOutboundAsync().WaitAsync(Deadline);

        PartialMessagesExtension? sent = eligibleRpcs.Select(rpc => rpc.Partial).SingleOrDefault(partial => partial is not null);
        Assert.Multiple(() =>
        {
            Assert.That(sent?.HasPartialMessage, Is.EqualTo(!metadataOnly));
            Assert.That(sent?.HasPartsMetadata, Is.EqualTo(metadataOnly));
            Assert.That(incompatibleRpcs.Select(rpc => rpc.Partial).OfType<PartialMessagesExtension>(), Is.Empty);
            Assert.That(state.Fanout[Topic], Is.EquivalentTo(new[] { sharedFanout ? legacy.PeerId : eligible.PeerId }));
        });
    }

    [Test]
    public async Task PublishFanout_ReachesFullMessagePeerAfterPartialPublish()
    {
        PubsubSettings settings = Settings();
        settings.EnablePartialMessages = true;
        settings.Degree = 1;
        settings.LowestDegree = 1;
        await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), new ScriptedRandom(0));
        await Start(router);
        router.GetPartialMessagesTopic(Topic, new PartialMessagesTopicOptions { SupportsSendingPartialMessages = true }, subscribe: false);
        ConcurrentQueue<Rpc> partialRpcs = [];
        ConcurrentQueue<Rpc> fullRpcs = [];
        var requester = await Open(router, 2, partialRpcs, partial: true);
        router.PublishPartial(Topic, [1], partialMessage: [2]);
        Assert.That(((IRoutingStateContainer)router).Fanout[Topic], Is.EquivalentTo(new[] { requester.PeerId }));
        await Open(router, 3, fullRpcs);

        router.Publish(Topic, [3]);
        await router.FlushOutboundAsync().WaitAsync(Deadline);

        Assert.Multiple(() =>
        {
            Assert.That(fullRpcs.SelectMany(rpc => rpc.Publish).Select(message => message.Data), Is.EqualTo(new[] { ByteString.CopyFrom([3]) }));
            Assert.That(partialRpcs.SelectMany(rpc => rpc.Publish), Is.Empty);
        });
    }

    [TestCase(2, 0, true)]
    [TestCase(1, 0.5, true)]
    [TestCase(100, 0, true)]
    [TestCase(2, 0, false)]
    [TestCase(1, 0.5, false)]
    [TestCase(100, 0, false)]
    public async Task HeartbeatGossip_SamplesRecipientsAboveGossipThreshold(int lazyDegree, double gossipFactor, bool subscribed)
    {
        PeerId[] eligible = Eligible(8);
        HashSet<PeerId> reached = [];
        for (int offset = 0; offset < eligible.Length; offset++)
        {
            ScriptedRandom random = new(offset);
            PubsubSettings settings = Settings();
            settings.Degree = 1;
            settings.LowestDegree = 0;
            settings.LazyDegree = lazyDegree;
            settings.GossipFactor = gossipFactor;
            await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), random);
            await Start(router);
            var existing = await Open(router, 4);
            if (subscribed)
            {
                router.Subscribe(Topic);
                await Graft(router, existing.PeerId);
            }
            router.Publish(Topic, [1]); // Cache a message and, if unsubscribed, fill fanout before adding candidates.
            random.Calls.Clear();
            Dictionary<PeerId, ConcurrentQueue<Rpc>> sent = [];
            foreach (int id in new[] { 1, 2 }.Concat(Enumerable.Range(10, eligible.Length)))
            {
                ConcurrentQueue<Rpc> messages = [];
                var peer = await Open(router, id, messages);
                sent.Add(peer.PeerId, messages);
            }
            router.SetAppSpecificScore(TestPeers.PeerId(2), settings.GossipThreshold - 1);
            router.SetAppSpecificScore(eligible[0], settings.GossipThreshold);

            await router.Heartbeat();
            await router.FlushOutboundAsync().WaitAsync(Deadline);

            PeerId[] selected = sent.Where(entry => entry.Value.Any(rpc => rpc.Control?.Ihave.Count > 0)).Select(entry => entry.Key).ToArray();
            int expected = Math.Min(eligible.Length, Math.Max(lazyDegree, (int)(eligible.Length * gossipFactor)));
            AssertSample(selected, eligible, expected, random);
            reached.UnionWith(selected);
        }
        Assert.That(reached, Is.EquivalentTo(eligible));
    }

    [TestCase(0, 0)]
    [TestCase(4, 0)]
    [TestCase(0, 2)]
    public async Task HeartbeatPrune_ShufflesScoreTiesAndSamplesRemainingPeers(int scoreCount, int outboundCount)
    {
        PeerId[] eligible = Eligible();
        HashSet<PeerId> reached = [];
        for (int offset = 0; offset < eligible.Length; offset++)
        {
            ScriptedRandom random = new(offset);
            PubsubSettings settings = Settings();
            settings.DScore = scoreCount;
            settings.DOut = outboundCount;
            settings.LowestDegree = 3;
            settings.Degree = 4;
            settings.HighestDegree = 5;
            settings.TopicScoreParams[Topic] = new TopicScoreParams { TimeInMeshWeight = 0 };
            await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), random);
            HashSet<PeerId> selected = await OpenOversizedMesh(router, eligible.Length);
            Assert.That(selected, Is.EquivalentTo(eligible));

            await router.Heartbeat();

            Assert.That(selected, Has.Count.EqualTo(settings.Degree));
            Assert.That(selected, Is.SubsetOf(eligible));
            var shuffleDraws = Enumerable.Range(0, eligible.Length - 1).Select(i => (i, eligible.Length));
            // All peers are outbound in this fixture, so no quota repair draws are needed.
            var remainingDraws = Enumerable.Range(0, settings.Degree - scoreCount)
                .Select(i => (i, eligible.Length - scoreCount));
            Assert.That(random.Calls, Is.EqualTo(shuffleDraws.Concat(remainingDraws)));
            reached.UnionWith(selected);
        }
        Assert.That(reached, Is.EquivalentTo(eligible));
    }

    [TestCase(4, 1, 1)]
    [TestCase(5, 2, 0)]
    [TestCase(5, 1, 1)]
    [TestCase(5, 2, 1)]
    public async Task HeartbeatPrune_TrimsToDegreeOnlyAboveHighestDegree(int peerCount, int scoreQuota, int outboundQuota)
    {
        PeerId[] eligible = Eligible(peerCount);
        ScriptedRandom random = new(0);
        PubsubSettings settings = Settings();
        settings.Degree = 2;
        settings.HighestDegree = 4;
        settings.DScore = scoreQuota;
        settings.DOut = outboundQuota;
        settings.TopicScoreParams[Topic] = new TopicScoreParams { TimeInMeshWeight = 0 };
        await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), random);
        HashSet<PeerId> mesh = await OpenOversizedMesh(router, peerCount, soleOutboundId: 10 + peerCount - 1);
        router.SetAppSpecificScore(eligible[0], 20);
        router.SetAppSpecificScore(eligible[1], 10);
        Assert.That(mesh, Is.EquivalentTo(eligible));

        await router.Heartbeat();

        if (peerCount == settings.HighestDegree)
        {
            Assert.That(mesh, Is.EquivalentTo(eligible));
            Assert.That(random.Calls, Is.Empty);
        }
        else
        {
            PeerId nextSurvivor = outboundQuota > 0 ? eligible[^1] : eligible[1];
            Assert.That(mesh, Is.EquivalentTo(new[] { eligible[0], nextSurvivor }));
        }
    }

    [Test]
    public async Task HeartbeatPrune_SamplesRemainingPeersIndependentlyOfScoreTies()
    {
        PeerId[] eligible = Eligible(4);
        HashSet<PeerId> topScoringPeers = [eligible[0], eligible[1]];
        List<PeerId> pruned = [];
        for (int offset = 0; offset < 6; offset++)
        {
            // Fix the score tie-break order, then exhaust all 3 * 2 choices for two open slots.
            ScriptedRandom random = new(offset, 0, 0, 0);
            PubsubSettings settings = Settings();
            settings.DScore = 1;
            settings.DOut = 0;
            settings.HighestDegree = 3;
            settings.TopicScoreParams[Topic] = new TopicScoreParams { TimeInMeshWeight = 0 };
            await using PubsubRouter router = new(new PeerStore(), settings, null, new Clock(), random);
            HashSet<PeerId> mesh = await OpenOversizedMesh(router, eligible.Length);
            foreach (PeerId peer in topScoringPeers) router.SetAppSpecificScore(peer, 10);
            PeerId reserved = mesh.First(topScoringPeers.Contains);

            await router.Heartbeat();

            Assert.That(mesh, Does.Contain(reserved));
            pruned.Add(eligible.Except(mesh).Single());
        }

        // Each of the three peers outside the score quota has an equal chance of being pruned.
        Assert.That(pruned.GroupBy(peer => peer).Select(group => group.Count()), Is.EquivalentTo(new[] { 2, 2, 2 }));
    }
}
