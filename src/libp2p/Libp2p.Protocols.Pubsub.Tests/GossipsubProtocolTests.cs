// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

[TestFixture]
public class GossipsubProtocolTests
{
    [Test]
    public async Task Test_New_messages_are_sent_to_mesh_only()
    {
        PubsubRouter router = new();
        Settings settings = new() { HeartbeatInterval = int.MaxValue };
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
}
