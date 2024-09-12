// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PubsubProtocolTests
{
    [Test]
    public async Task Test_Peer_is_dialed_when_added_by_discovery()
    {
        PubsubRouter router = new();
        IRoutingStateContainer state = router;
        Multiaddress discoveredPeer = TestPeers.Multiaddr(1);
        PeerId peerId = TestPeers.PeerId(1);
        Multiaddress localPeer = TestPeers.Multiaddr(2);

        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Address.Returns(localPeer);
        peer.DialAsync(discoveredPeer, Arg.Any<CancellationToken>()).Returns(new TestRemotePeer(discoveredPeer));

        TestDiscoveryProtocol discovery = new();
        CancellationToken token = default;
        TaskCompletionSource taskCompletionSource = new();

        _ = router.RunAsync(peer, discovery, token: token);
        discovery.OnAddPeer!([discoveredPeer]);

        await Task.Delay(100);
        _ = peer.Received().DialAsync(discoveredPeer, Arg.Any<CancellationToken>());

        router.OutboundConnection(discoveredPeer, PubsubRouter.FloodsubProtocolVersion, taskCompletionSource.Task, (rpc) => { });
        Assert.That(state.ConnectedPeers, Has.Member(peerId));
        taskCompletionSource.SetResult();
    }
}
