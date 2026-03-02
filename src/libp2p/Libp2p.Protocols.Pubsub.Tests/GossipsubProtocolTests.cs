// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class GossipsubProtocolTests
{
    [Test]
    public async Task Test_New_messages_are_sent_to_mesh_only()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        PubsubSettings settings = new() { HeartbeatInterval = int.MaxValue };
        IRoutingStateContainer state = router;
        int peerCount = PubsubSettings.Default.HighestDegree + 1;
        const string commonTopic = "topic1";

        ILocalPeer peer = new LocalPeerStub();
        List<Rpc> sentRpcs = [];

        router.GetTopic(commonTopic);
        Assert.That(state.FloodsubPeers.Keys, Has.Member(commonTopic));
        Assert.That(state.GossipsubPeers.Keys, Has.Member(commonTopic));
        await router.StartAsync(peer);
        TaskCompletionSource tcs = new();

        foreach (int index in Enumerable.Range(1, peerCount))
        {
            Multiaddress discoveredPeer = TestPeers.Multiaddr(index);
            PeerId peerId = TestPeers.PeerId(index);

            peerStore.Discover([discoveredPeer]);
            router.OutboundConnection(discoveredPeer, PubsubRouter.GossipsubProtocolVersionV10, tcs.Task, sentRpcs.Add);
            router.InboundConnection(discoveredPeer, PubsubRouter.GossipsubProtocolVersionV10, tcs.Task, tcs.Task, () => Task.CompletedTask);
            router.OnRpc(peerId, new Rpc().WithTopics([commonTopic], []));
        }

        await router.Heartbeat();

        Assert.Multiple(() =>
        {
            Assert.That(state.GossipsubPeers[commonTopic], Has.Count.EqualTo(peerCount));
            Assert.That(state.Mesh[commonTopic], Has.Count.EqualTo(PubsubSettings.Default.Degree));
        });

        tcs.SetResult();
    }
}
