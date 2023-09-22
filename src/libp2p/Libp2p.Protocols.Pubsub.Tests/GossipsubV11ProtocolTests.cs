// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;


[TestFixture]
public class GossipsubV11ProtocolTests
{
    // Explicit Peering Agreements
    [Test]
    public async Task Test_ExplicitPeers_Setup()
    {
        PubsubRouter router = new();
        Settings settings = new() { HeartbeatIntervalMs = int.MaxValue,  };
        IRoutingStateContainer state = router;
        int peerCount = Settings.Default.Degree * 2;
        const string commonTopic = "topic1";

        ILocalPeer peer = new TestLocalPeer();
        TestDiscoveryProtocol discovery = new();
        CancellationToken token = default;
        List<Rpc> sentRpcs = new();

        _ = router.RunAsync(peer, discovery, token: token);
        router.Subscribe(commonTopic);
        Assert.That(state.FloodsubPeers.Keys, Has.Member(commonTopic));
        Assert.That(state.GossipsubPeers.Keys, Has.Member(commonTopic));

        foreach (var index in Enumerable.Range(1, peerCount))
        {
            Multiaddr discoveredPeer = TestPeers.Multiaddr(index);
            PeerId peerId = TestPeers.PeerId(index);

            discovery.OnAddPeer!(new[] { discoveredPeer });
            router.OutboundConnection(peerId, PubsubRouter.GossipsubProtocolVersionV10, sentRpcs.Add);
            router.InboundConnection(peerId, PubsubRouter.GossipsubProtocolVersionV10, () => { });
            router.OnRpc(peerId, new Rpc().WithTopics(new[] { commonTopic }, Enumerable.Empty<string>()));
        }

        await router.Heartbeat();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[commonTopic], Has.Count.EqualTo(peerCount));
            Assert.That(state.Mesh[commonTopic], Has.Count.EqualTo(Settings.Default.Degree));
        });
    }

    [Test]
    public async Task Test_ExplicitPeers_AllConnected()
    {

    }
    [Test]
    public async Task Test_ExplicitPeers_AreReconnected() // after 5 minutes
    {

    }
    [Test]
    public async Task Test_ExplicitPeers_AreNotAddedToMesh()
    {

    }
    [Test]
    public async Task Test_ExplicitPeers_DoNotHaveScore()
    {

    }
    [Test]
    public async Task Test_ExplicitPeers_DeclineGraftWithPrune()
    {

    }

    // PRUNE Backoff and Peer Exchange
    [Test]
    public async Task Test_PeerProvidesPeers_OnPruneIfEnabled()
    {

    }
    [Test]
    public async Task Test_PeerDoesNotProvidePeers_OnPruneIfDisabled()
    {

    }
    [Test]
    public async Task Test_PeerProvidesLottaPeers_OnPrune() // > D_hi
    {

    }
    [Test]
    public async Task Test_PeerAutoprunes_ForGraftDuringBackoff() // 1 minute
    {

    }
    [Test]
    public async Task Test_PeerPenalizes_ForGraftDuringBackoff()
    {

    }
    [Test]
    public async Task Test_PeerDoesNotAutoprune_ForGraftAfterBackoff()
    {

    }
    [Test]
    public async Task Test_PeerDoesNotPenalize_ForGraftAfterBackoff()
    {

    }

    // Flood Publishing
    [Test]
    public async Task Test_PeerFloodsAllPeers_IfEnabled()
    {

    }
    [Test]
    public async Task Test_PeerFloodsAllPeers_WithGoodScore()
    {

    }
    [Test]
    public async Task Test_PeerSendsToMesh_IfDisabled()
    {

    }
    [Test]
    public async Task Test_PeerSendsToFanout_IfDisabled()
    {

    }

    [Test]
    public async Task Test_PeerSendsOwnMessage_ToAllTopicPeers()
    {

    }

    // Adaptive Gossip Dissemination
    // Outbound Mesh Quotas
    // Peer Scoring
    // Score Thresholds
    // Heartbeat Maintenance
    // Opportunistic Grafting
    // The Score Function
    // Topic Parameter Calculation and Decay
    // Extended Validators
    // Spam Protection Measures
    // Recommendations for Network Operators
    [Test]
    public async Task Test_()
    {

    }
}
