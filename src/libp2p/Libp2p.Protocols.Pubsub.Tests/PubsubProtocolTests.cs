//// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
//// SPDX-License-Identifier: MIT

//namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PubsubProtocolTests
{
    [Test]
    public async Task Test_Peer_is_dialed_when_added_by_discovery()
    {
        //PeerStore peerStore = new();
        //PubsubRouter router = new(peerStore);
        //IRoutingStateContainer state = router;
        //Multiaddress discoveredPeerAddr = TestPeers.Multiaddr(1);
        //Multiaddress localPeer = TestPeers.Multiaddr(2);

        //ILocalPeer peer = Substitute.For<ILocalPeer>();
        //peer.Address.Returns(localPeer);
        //peer.Identity.Returns(TestPeers.Identity(2));
        //peer.DialAsync(discoveredPeerAddr, Arg.Any<CancellationToken>()).Returns(new TestRemotePeer(discoveredPeerAddr));

        //CancellationToken token = default;
        //TaskCompletionSource taskCompletionSource = new();

        //_ = router.RunAsync(peer, token: token);
        //peerStore.Discover([discoveredPeerAddr]);

        //await Task.Delay(100);
        //_ = peer.Received().DialAsync(discoveredPeerAddr, Arg.Any<CancellationToken>());

        //router.OutboundConnection(discoveredPeerAddr, PubsubRouter.FloodsubProtocolVersion, taskCompletionSource.Task, (rpc) => { });
        //Assert.That(state.ConnectedPeers, Has.Member(discoveredPeerAddr.GetPeerId()));
        //taskCompletionSource.SetResult();
    }
}
