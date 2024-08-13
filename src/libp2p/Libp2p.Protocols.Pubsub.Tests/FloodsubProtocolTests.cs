// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

[TestFixture]
public class FloodsubProtocolTests
{
    [Test]
    public async Task Test_Peer_is_in_fpeers()
    {
        PubsubRouter router = new();
        IRoutingStateContainer state = router;
        Multiaddress discoveredPeer = TestPeers.Multiaddr(1);
        PeerId peerId = TestPeers.PeerId(1);
        Multiaddress localPeerAddr = TestPeers.Multiaddr(2);
        const string commonTopic = "topic1";

        IPeer peer = Substitute.For<IPeer>();
        peer.Address.Returns(localPeerAddr);
        peer.DialAsync(discoveredPeer, Arg.Any<CancellationToken>()).Returns(new TestRemotePeer(discoveredPeer));

        TestDiscoveryProtocol discovery = new();
        CancellationToken token = default;
        List<Rpc> sentRpcs = new();

        _ = router.RunAsync(peer, discovery, token: token);
        router.Subscribe(commonTopic);
        Assert.That(state.FloodsubPeers.Keys, Has.Member(commonTopic));

        discovery.OnAddPeer!([discoveredPeer]);
        await Task.Delay(100);
        _ = peer.Received().DialAsync(discoveredPeer, Arg.Any<CancellationToken>());

        TaskCompletionSource tcs = new();

        router.OutboundConnection(discoveredPeer, PubsubRouter.FloodsubProtocolVersion, tcs.Task, sentRpcs.Add);
        router.InboundConnection(discoveredPeer, PubsubRouter.FloodsubProtocolVersion, tcs.Task, tcs.Task, () => Task.CompletedTask);
        await router.OnRpc(peerId, new Rpc().WithTopics(new[] { commonTopic }, Enumerable.Empty<string>()));

        Assert.Multiple(() =>
        {
            Assert.That(state.FloodsubPeers[commonTopic], Has.Member(peerId));
            Assert.That(sentRpcs.Any(rpc => rpc.Subscriptions.Any(s => s.Subscribe && s.Topicid == commonTopic)), Is.True);
        });

        tcs.SetResult();
    }
}
