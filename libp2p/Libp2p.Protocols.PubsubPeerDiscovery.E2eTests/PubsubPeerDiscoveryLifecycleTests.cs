// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;
using NSubstitute;
using NUnit.Framework;
using System.Collections.ObjectModel;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

[TestFixture]
public class PubsubPeerDiscoveryLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task DisposeAsync_stops_broadcasting_and_releases_topics_without_caller_cancellation()
    {
        PeerStore peerStore = new();
        await using PubsubRouter router = new(peerStore);
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Identity.Returns(TestPeers.Identity(0));
        peer.ListenAddresses.Returns(new ObservableCollection<Multiaddress> { TestPeers.Multiaddr(0) });
        await router.StartAsync(peer);

        PubsubPeerDiscoverySettings settings = new() { Interval = 20 };
        PubsubPeerDiscoveryProtocol discovery = new(router, peerStore, settings, peer);
        await discovery.StartDiscoveryAsync([TestPeers.Multiaddr(0)]);

        ITopic topic = router.GetTopic(settings.Topics[0], subscribe: false);
        Assert.That(topic.IsSubscribed, Is.True);

        await discovery.DisposeAsync().AsTask().WaitAsync(Timeout);
        await discovery.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.That(topic.IsSubscribed, Is.False);

        // The broadcast loop reads ListenAddresses on every tick; once disposed it must not tick again.
        peer.ClearReceivedCalls();
        await Task.Delay(settings.Interval * 5);
        _ = peer.DidNotReceive().ListenAddresses;
    }
}
