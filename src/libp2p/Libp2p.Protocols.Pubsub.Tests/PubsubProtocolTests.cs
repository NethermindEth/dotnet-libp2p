// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;

[TestFixture]
public class PubsubProtocolTests
{
    [Test]
    public void Publish_WhenRouterNotStarted_ThrowsInvalidOperationException()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);

        InvalidOperationException? ex = Assert.Throws<InvalidOperationException>(() => router.Publish("test-topic", [1, 2, 3]));
        Assert.That(ex!.Message, Is.EqualTo("Router has not been started. Call StartAsync() first."));
    }

    [Test]
    public void Publish_WithNullTopicId_ThrowsArgumentNullException()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);

        Assert.Throws<ArgumentNullException>(() => router.Publish(null!, [1, 2, 3]));
    }

    [Test]
    public void Publish_WithNullMessage_ThrowsArgumentNullException()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);

        Assert.Throws<ArgumentNullException>(() => router.Publish("test-topic", null!));
    }
    [Test]
    public async Task Test_Peer_is_dialed_when_added_by_discovery()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        IRoutingStateContainer state = router;
        Multiaddress localPeerAddr = TestPeers.Multiaddr(1);
        Multiaddress[] discoveredPeerAddrs = [TestPeers.Multiaddr(2)];

        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.ListenAddresses.Returns([localPeerAddr]);
        peer.Identity.Returns(TestPeers.Identity(1));
        peer.DialAsync(discoveredPeerAddrs, Arg.Any<CancellationToken>()).Returns(new TestRemotePeer(discoveredPeerAddrs[0]));

        CancellationToken token = default;
        TaskCompletionSource taskCompletionSource = new();

        await router.StartAsync(peer, token: token);
        peerStore.Discover(discoveredPeerAddrs);

        await Task.Delay(100);
        _ = peer.Received().DialAsync(discoveredPeerAddrs, Arg.Any<CancellationToken>());

        router.OutboundConnection(discoveredPeerAddrs[0], PubsubRouter.FloodsubProtocolVersion, taskCompletionSource.Task, (rpc) => { });
        Assert.That(state.ConnectedPeers, Has.Member(discoveredPeerAddrs[0].GetPeerId()));
        taskCompletionSource.SetResult();
    }
}
