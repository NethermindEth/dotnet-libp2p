// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework.Internal;

namespace Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Tests;

[TestFixture, Ignore("No support of time mock yet")]
[Parallelizable(scope: ParallelScope.All)]
public class MultistreamProtocolTests
{
    [Test, CancelAfter(5000)]
    public async Task Test_PeersConnect()
    {
        IPeerFactory peerFactory = new TestBuilder().Build();
        ChannelBus commonBus = new();

        ServiceProvider sp1 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton(commonBus)
             .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        ServiceProvider sp2 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton(commonBus)
             .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        IPeer peerA = sp1.GetService<IPeerFactory>()!.Create(TestPeers.Identity(1));
        await peerA.StartListenAsync([TestPeers.Multiaddr(1)]);
        IPeer peerB = sp2.GetService<IPeerFactory>()!.Create(TestPeers.Identity(2));
        await peerB.StartListenAsync([TestPeers.Multiaddr(2)]);

        ISession remotePeerB = await peerA.DialAsync(peerB.ListenAddresses.ToArray());
        await remotePeerB.DialAsync<GossipsubProtocol>();
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        int totalCount = 5;
        TestContextLoggerFactory fac = new();
        // There is common communication point
        ChannelBus commonBus = new(fac);
        IPeer[] peers = new IPeer[totalCount];
        PeerStore[] peerStores = new PeerStore[totalCount];
        PubsubRouter[] routers = new PubsubRouter[totalCount];

        for (int i = 0; i < totalCount; i++)
        {
            // But we create a seprate setup for every peer
            ServiceProvider sp = new ServiceCollection()
                   .AddSingleton<ILoggerFactory>(sp => fac)
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton<PeerStore>()
                   .AddSingleton(commonBus)
                   .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
            IPeer peer = peers[i] = peerFactory.Create(TestPeers.Identity(i));
            PubsubRouter router = routers[i] = sp.GetService<PubsubRouter>()!;
            PeerStore peerStore = sp.GetService<PeerStore>()!;
            PubsubPeerDiscoveryProtocol disc = new(router, peerStore, new PubsubPeerDiscoverySettings() { Interval = 300 }, peer);
            await peer.StartListenAsync([TestPeers.Multiaddr(i)]);
            _ = router.RunAsync(peer);
            peerStores[i] = peerStore;
            _ = disc.DiscoverAsync(peers[i].ListenAddresses);
        }

        await Task.Delay(1000);

        for (int i = 0; i < peers.Length; i++)
        {
            peerStores[i].Discover(peers[(i + 1) % totalCount].ListenAddresses.ToArray());
        }

        await Task.Delay(30000);

        foreach (PubsubRouter router in routers)
        {
            Assert.That(((IRoutingStateContainer)router).ConnectedPeers.Count, Is.EqualTo(totalCount - 1));
        }
    }

    [Test, CancelAfter(5000)]
    public async Task Test_ConnectionEstablished_AfterHandshak3e()
    {
        int totalCount = 5;

        PubsubTestSetup setup = new();
        Dictionary<int, PubsubPeerDiscoveryProtocol> discoveries = [];

        await setup.AddAsync(totalCount);

        // discover in circle
        for (int i = 0; i < setup.Peers.Count; i++)
        {
            setup.PeerStores[i].Discover(setup.Peers[(i + 1) % setup.Peers.Count].ListenAddresses.ToArray());
        }

        for (int i = 0; i < setup.Peers.Count; i++)
        {
            discoveries[i] = new(setup.Routers[i], setup.PeerStores[i], new PubsubPeerDiscoverySettings() { Interval = int.MaxValue }, setup.Peers[i]);
            _ = discoveries[i].DiscoverAsync(setup.Peers[i].ListenAddresses);
        }

        await Task.Delay(100);

        await setup.Heartbeat();
        await setup.Heartbeat();
        await setup.Heartbeat();

        for (int i = 0; i < setup.Peers.Count; i++)
        {
            discoveries[i].BroadcastPeerInfo();
        }

        foreach (PubsubRouter router in setup.Routers.Values)
        {
            Assert.That(((IRoutingStateContainer)router).ConnectedPeers.Count, Is.EqualTo(totalCount - 1));
        }
    }
}
