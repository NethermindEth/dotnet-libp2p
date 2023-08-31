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
        //var settings = new Settings() { HeartbeatInterval = int.MaxValue };
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
            router.OutboundConnection(peerId, PubsubRouter.FloodsubProtocolVersion, sentRpcs.Add);
            router.InboundConnection(peerId, PubsubRouter.FloodsubProtocolVersion, () => { });
            router.OnRpc(peerId, new Rpc().WithTopics(new[] { commonTopic }, Enumerable.Empty<string>()));
        }

        router.Publish(commonTopic, Array.Empty<byte>());

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[commonTopic].Count, Is.EqualTo(peerCount));
            Assert.That(state.Mesh[commonTopic].Count, Is.EqualTo(Settings.Default.Degree));
            //Assert.That(sentRpcs.Any(rpc => rpc.Subscriptions.Any(s => s.Subscribe && s.Topicid == commonTopic)), Is.True);
        });
    }
}
