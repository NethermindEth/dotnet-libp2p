// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
[CancelAfter(20_000)]
public class OutboundMeshQuotaTests
{
    [Test]
    public async Task GraftAtCapacityUsesConnectionDirection(
        [Values(ConnectionDirection.Inbound, ConnectionDirection.Outbound, ConnectionDirection.Unknown)] ConnectionDirection direction,
        [Values(7, 8, 9)] int size)
    {
        await using MeshTest test = new();
        await test.Seed(size);
        var candidate = await test.Add(20, direction);
        await test.Graft(candidate);
        bool accepted = size < test.Settings.HighestDegree || direction == ConnectionDirection.Outbound;
        Assert.Multiple(() =>
        {
            Assert.That(test.Mesh.Contains(candidate.PeerId), Is.EqualTo(accepted));
            Assert.That(test.Prunes(candidate).Count(), Is.EqualTo(accepted ? 0 : 1));
            Assert.That(candidate.Backoff.ContainsKey("topic"), Is.EqualTo(!accepted));
            Assert.That(candidate.Score.GetTopicScore("topic").MeshJoinTime is not null, Is.EqualTo(accepted));
        });
    }

    [TestCase("backoff")]
    [TestCase("score")]
    [TestCase("direct")]
    [TestCase("inactive")]
    public async Task OutboundGraftStillHonorsEligibility(string rejection)
    {
        await using MeshTest test = new(new PubsubSettings { DirectPeers = rejection == "direct" ? [TestPeers.Multiaddr(20)] : [] });
        await test.Seed(8);
        var candidate = await test.Add(20, ConnectionDirection.Outbound);
        if (rejection == "backoff") candidate.Backoff["topic"] = DateTime.UtcNow.AddHours(1);
        if (rejection == "score") test.Router.SetAppSpecificScore(candidate.PeerId, -1);
        if (rejection == "inactive") test.Router.Unsubscribe("topic");
        await test.Graft(candidate);
        Assert.That(test.Mesh.Contains(candidate.PeerId), Is.False);
        if (rejection != "inactive") Assert.That(test.Prunes(candidate).Count(), Is.EqualTo(1));
        if (rejection == "backoff") Assert.That(candidate.Score.BehaviorPenalty, Is.EqualTo(1));
    }

    [Test]
    public async Task RepeatedGraftAtCapacityDoesNotPruneAMeshMember()
    {
        await using MeshTest test = new();
        await test.Seed(8);
        var peer = test.Peers[1];
        await test.Graft(peer);
        Assert.Multiple(() =>
        {
            Assert.That(test.Mesh, Has.Count.EqualTo(8));
            Assert.That(test.Mesh, Does.Contain(peer.PeerId));
            Assert.That(test.Prunes(peer), Is.Empty);
            Assert.That(peer.Backoff, Is.Empty);
        });
    }

    [Test]
    public async Task HeartbeatFillsOnlyEligibleOutboundPeers(
        [Values(4, 8)] int size, [Values(0, 1, 2)] int outbound, [Values(0, 2)] int quota)
    {
        PubsubSettings settings = new() { DOut = quota, DirectPeers = [TestPeers.Multiaddr(30)] };
        await using MeshTest test = new(settings);
        await test.Seed(size, outbound);
        await test.Add(10, ConnectionDirection.Inbound);
        await test.Add(11, ConnectionDirection.Unknown);
        var backedOff = await test.Add(12, ConnectionDirection.Outbound);
        backedOff.Backoff["topic"] = DateTime.UtcNow.AddHours(1);
        var negative = await test.Add(13, ConnectionDirection.Outbound);
        test.Router.SetAppSpecificScore(negative.PeerId, -1);
        await test.Add(30, ConnectionDirection.Outbound);
        var first = await test.Add(20, ConnectionDirection.Outbound);
        var second = await test.Add(21, ConnectionDirection.Outbound);
        await test.Add(22, ConnectionDirection.Outbound, subscribe: false);
        PeerId[] before = test.Mesh.ToArray();

        await test.Heartbeat();

        int missing = Math.Max(0, quota - outbound);
        Assert.That(test.Mesh, Has.Count.EqualTo(size + missing));
        Assert.That(test.Mesh.Except(before), Is.EquivalentTo(new[] { first.PeerId, second.PeerId }.Take(missing)));
        Assert.That(test.Sent.Values.SelectMany(rpcs => rpcs).SelectMany(rpc => rpc.Control?.Graft ?? []).Count(), Is.EqualTo(missing));
        foreach (var peer in test.Peers.Values.Where(p => !before.Contains(p.PeerId)))
            Assert.That(peer.Score.GetTopicScore("topic").MeshJoinTime is not null, Is.EqualTo(test.Mesh.Contains(peer.PeerId)));
    }

    [Test]
    public async Task SmallMeshFillsDegreeThenOutboundQuota()
    {
        await using MeshTest test = new();
        await test.Seed(3);
        for (int id = 4; id <= 6; id++) await test.Add(id, ConnectionDirection.Inbound);
        var first = await test.Add(20, ConnectionDirection.Outbound);
        var second = await test.Add(21, ConnectionDirection.Outbound);
        await test.Heartbeat();
        Assert.Multiple(() =>
        {
            Assert.That(test.Mesh, Has.Count.EqualTo(8));
            Assert.That(test.Mesh, Does.Contain(first.PeerId));
            Assert.That(test.Mesh, Does.Contain(second.PeerId));
        });
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(5)]
    public async Task PruningKeepsExactlyDegreeAndAvailableOutboundQuota(int outbound)
    {
        for (int iteration = 0; iteration < 20; iteration++)
        {
            await using MeshTest test = new();
            await test.Seed(9, outbound);
            foreach ((int id, var peer) in test.Peers) test.Router.SetAppSpecificScore(peer.PeerId, 10 - id);
            await test.Heartbeat();
            Assert.That(test.Mesh, Has.Count.EqualTo(6));
            Assert.That(test.Peers.Values.Count(p => p.Direction == ConnectionDirection.Outbound && test.Mesh.Contains(p.PeerId)),
                Is.GreaterThanOrEqualTo(Math.Min(2, outbound)));
            Assert.That(test.Mesh, Is.SupersetOf(Enumerable.Range(1, 4).Select(TestPeers.PeerId)), "D_score survives when it leaves room for D_out.");
            foreach (var peer in test.Peers.Values)
            {
                bool pruned = !test.Mesh.Contains(peer.PeerId);
                Assert.That(test.Prunes(peer).Count(), Is.EqualTo(pruned ? 1 : 0));
                Assert.That(peer.Backoff.ContainsKey("topic"), Is.EqualTo(pruned));
                Assert.That(peer.Score.GetTopicScore("topic").MeshJoinTime is null, Is.EqualTo(pruned));
            }
        }
    }

    [Test]
    public async Task OutboundQuotaReplacesLowestScorersWhenAllDegreeSlotsAreProtected()
    {
        await using MeshTest test = new();
        test.Settings.DScore = 6;
        await test.Seed(9, 2);
        foreach ((int id, var peer) in test.Peers) test.Router.SetAppSpecificScore(peer.PeerId, 10 - id);
        await test.Heartbeat();
        Assert.That(test.Mesh, Is.EquivalentTo(new[] { 1, 2, 3, 4, 8, 9 }.Select(TestPeers.PeerId)));
    }

    [TestCase(ConnectionDirection.Inbound)]
    [TestCase(ConnectionDirection.Unknown)]
    public async Task MixedDirectionsStayUnknownAndDoNotSatisfyQuota(ConnectionDirection other)
    {
        await using MeshTest test = new();
        await test.Seed(8);
        var candidate = await test.Add(20, ConnectionDirection.Outbound);
        test.Router.InboundConnection(candidate.Address, PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
            () => Task.CompletedTask, () => Task.CompletedTask, other);
        test.Router.InboundConnection(candidate.Address, PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
            () => Task.CompletedTask, () => Task.CompletedTask, ConnectionDirection.Outbound);
        Assert.That(candidate.Direction, Is.EqualTo(ConnectionDirection.Unknown));
        await test.Heartbeat();
        Assert.That(test.Mesh, Does.Not.Contain(candidate.PeerId));
        await test.Graft(candidate);
        Assert.That(test.Mesh, Does.Not.Contain(candidate.PeerId));
        candidate.Backoff.Clear();
        test.Mesh.Add(candidate.PeerId);
        await test.Add(21, ConnectionDirection.Outbound);
        await test.Add(22, ConnectionDirection.Outbound);
        // Stay at D_hi to avoid pruning, then prove Unknown contributes no quota credit.
        test.Mesh.Remove(TestPeers.PeerId(1));
        await test.Heartbeat();
        Assert.That(test.Mesh, Has.Count.EqualTo(10));
    }

    [Test]
    public async Task RejectedDuplicateWriterDoesNotChangeDirection()
    {
        await using MeshTest test = new();
        var peer = await test.Add(20, ConnectionDirection.Outbound);
        var duplicate = test.Router.OutboundConnection(peer.Address, PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
            (_, _) => ValueTask.FromResult(IOResult.Ok), () => ValueTask.CompletedTask, () => Task.CompletedTask, ConnectionDirection.Inbound);
        Assert.That(duplicate, Is.Null);
        Assert.That(peer.Direction, Is.EqualTo(ConnectionDirection.Outbound));
    }

    private sealed class MeshTest : IAsyncDisposable
    {
        public PubsubSettings Settings { get; }
        public PubsubRouter Router { get; }
        public HashSet<PeerId> Mesh => ((IRoutingStateContainer)Router).Mesh.GetValueOrDefault("topic") ?? [];
        public Dictionary<int, PubsubRouter.PubsubPeer> Peers { get; } = [];
        public Dictionary<PeerId, List<Rpc>> Sent { get; } = [];

        public MeshTest(PubsubSettings? settings = null)
        {
            Settings = settings ?? new PubsubSettings();
            Settings.Degree = 6;
            Settings.LowestDegree = 4;
            Settings.HighestDegree = 8;
            Settings.DScore = 4;
            Settings.AutoConnect = false;
            Settings.DecayInterval = int.MaxValue;
            Settings.AppSpecificWeight = 1;
            Router = new(new PeerStore(), Settings);
            Router.GetTopic("topic");
        }

        public async Task<PubsubRouter.PubsubPeer> Add(int id, ConnectionDirection direction, bool subscribe = true)
        {
            List<Rpc> frames = [];
            var peer = Router.OutboundConnection(TestPeers.Multiaddr(id), PubsubRouter.GossipsubProtocolVersionV11, new TaskCompletionSource().Task,
                (bytes, _) =>
                {
                    frames.Add(RouterTestExtensions.Decode(bytes));
                    return ValueTask.FromResult(IOResult.Ok);
                }, () => ValueTask.CompletedTask, () => Task.CompletedTask, direction)!;
            Peers[id] = peer;
            Sent[peer.PeerId] = frames;
            if (subscribe) await Router.OnRpcAsync(peer.PeerId, new Rpc().WithTopics(["topic"], []));
            await Flush();
            frames.Clear();
            return peer;
        }

        public async Task Seed(int size, int outbound = 0)
        {
            for (int id = 1; id <= size; id++)
            {
                var peer = await Add(id, id > size - outbound ? ConnectionDirection.Outbound : ConnectionDirection.Inbound);
                Mesh.Add(peer.PeerId);
                peer.Score.GetTopicScore("topic").JoinMesh();
            }
        }

        public async Task Graft(PubsubRouter.PubsubPeer peer)
        {
            await Router.OnRpcAsync(peer.PeerId, new Rpc { Control = new ControlMessage { Graft = { new ControlGraft { TopicID = "topic" } } } });
            await Flush();
        }

        public async Task Heartbeat()
        {
            await Router.Heartbeat();
            await Flush();
        }

        private Task Flush() => Router.FlushOutboundAsync().WaitAsync(TestContext.CurrentContext.CancellationToken);
        public IEnumerable<ControlPrune> Prunes(PubsubRouter.PubsubPeer peer) => Sent[peer.PeerId].SelectMany(rpc => rpc.Control?.Prune ?? []);
        public ValueTask DisposeAsync() => Router.DisposeAsync();
    }
}
