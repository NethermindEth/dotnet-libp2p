// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PubsubProtocolTests
{
    [Test]
    public async Task Test_Peer_is_dialed_when_added_by_discovery()
    {
        PubsubRouter router = new();
        IRoutingStateContainer state = router;
        Multiaddr discoveredPeer = TestPeers.Multiaddr(1);
        PeerId peerId = TestPeers.PeerId(1);

        ILocalPeer peer = Substitute.For<ILocalPeer>();
        TestDiscoveryProtocol discovery = new();
        CancellationToken token = default;

        _ = router.RunAsync(peer, discovery, token: token);
        discovery.OnAddPeer!(new[] { discoveredPeer });
        router.OutboundConnection(peerId, PubsubRouter.FloodsubProtocolVersion, (rpc) => { });

        await peer.Received().DialAsync(discoveredPeer);
        Assert.That(state.ConnectedPeers, Has.Member(peerId));
    }
}
