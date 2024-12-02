// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class FloodsubProtocolTests
{
    [Test]
    public async Task Test_Peer_is_in_fpeers()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        IRoutingStateContainer state = router;

        Identity discoveredPeer = TestPeers.Identity(1);
        Multiaddress discoveredPeerAddress = TestPeers.Multiaddr(1);
        Multiaddress localPeerAddr = TestPeers.Multiaddr(2);

        const string commonTopic = "topic1";

        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Address.Returns(localPeerAddr);
        peer.Identity.Returns(TestPeers.Identity(2));
        peer.DialAsync(discoveredPeerAddress, Arg.Any<CancellationToken>()).Returns(new TestRemotePeer(discoveredPeerAddress));
        IPeer peer = Substitute.For<IPeer>();
        peer.ListenAddresses.Returns([localPeerAddr]);
        peer.DialAsync(discoveredPeer, Arg.Any<CancellationToken>()).Returns(new TestRemotePeer(discoveredPeer));

        CancellationToken token = default;
        List<Rpc> sentRpcs = new();

        _ = router.RunAsync(peer, token: token);
        router.GetTopic(commonTopic);
        Assert.That(state.FloodsubPeers.Keys, Has.Member(commonTopic));

        peerStore.Discover([discoveredPeerAddress]);
        await Task.Delay(100);
        _ = peer.Received().DialAsync(discoveredPeerAddress, Arg.Any<CancellationToken>());

        TaskCompletionSource tcs = new();

        router.OutboundConnection(discoveredPeerAddress, PubsubRouter.FloodsubProtocolVersion, tcs.Task, sentRpcs.Add);
        router.InboundConnection(discoveredPeerAddress, PubsubRouter.FloodsubProtocolVersion, tcs.Task, tcs.Task, () => Task.CompletedTask);
        await router.OnRpc(discoveredPeer.PeerId, new Rpc().WithTopics(new[] { commonTopic }, []));

        Assert.Multiple(() =>
        {
            Assert.That(state.FloodsubPeers[commonTopic], Has.Member(discoveredPeer.PeerId));
            Assert.That(sentRpcs.Any(rpc => rpc.Subscriptions.Any(s => s.Subscribe && s.Topicid == commonTopic)), Is.True);
        });

        tcs.SetResult();
    }
}
