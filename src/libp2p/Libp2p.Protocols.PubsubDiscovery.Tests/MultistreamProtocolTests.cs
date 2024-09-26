// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework.Internal;

namespace Nethermind.Libp2p.Protocols.PubsubDiscovery.Tests;

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
             .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        ServiceProvider sp2 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        ILocalPeer peerA = sp1.GetService<IPeerFactory>()!.Create(TestPeers.Identity(1));
        await peerA.ListenAsync(TestPeers.Multiaddr(1));
        ILocalPeer peerB = sp2.GetService<IPeerFactory>()!.Create(TestPeers.Identity(2));
        await peerB.ListenAsync(TestPeers.Multiaddr(2));

        IRemotePeer remotePeerB = await peerA.DialAsync(peerB.Address);
        await remotePeerB.DialAsync<GossipsubProtocol>();
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        int totalCount = 5;
        TestContextLoggerFactory fac = new();
        // There is common communication point
        ChannelBus commonBus = new(fac);
        ILocalPeer[] peers = new ILocalPeer[totalCount];
        PeerStore[] peerStores = new PeerStore[totalCount];
        PubsubRouter[] routers = new PubsubRouter[totalCount];

        for (int i = 0; i < totalCount; i++)
        {
            // But we create a seprate setup for every peer
            ServiceProvider sp = new ServiceCollection()
                   .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
                   .AddSingleton<ILoggerFactory>(sp => fac)
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton<PeerStore>()
                   .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
            ILocalPeer peer = peers[i] = peerFactory.Create(TestPeers.Identity(i));
            PubsubRouter router = routers[i] = sp.GetService<PubsubRouter>()!;
            PeerStore peerStore = sp.GetService<PeerStore>()!;
            PubSubDiscoveryProtocol disc = new(router, peerStore, new PubSubDiscoverySettings() { Interval = 300 }, peer);
            await peer.ListenAsync(TestPeers.Multiaddr(i));
            _ = router.RunAsync(peer);
            peerStores[i] = peerStore;
            _ = disc.DiscoverAsync(peers[i].Address);
        }

        await Task.Delay(1000);

        for (int i = 0; i < peers.Length; i++)
        {
            peerStores[i].Discover([peers[(i + 1) % totalCount].Address]);
        }

        await Task.Delay(30000);

        foreach (var router in routers)
        {
            Assert.That(((IRoutingStateContainer)router).ConnectedPeers.Count, Is.EqualTo(totalCount - 1));
        }
    }

    [Test, CancelAfter(5000)]
    public async Task Test_ConnectionEstablished_AfterHandshak3e()
    {
        int totalCount = 5;

        PubSubTestSetup setup = new();
        Dictionary<int, PubSubDiscoveryProtocol> discoveries = [];

        await setup.AddAsync(totalCount);

        // discover in circle
        for (int i = 0; i < setup.Peers.Count; i++)
        {
            setup.PeerStores[i].Discover([setup.Peers[(i + 1) % setup.Peers.Count].Address]);
        }

        for (int i = 0; i < setup.Peers.Count; i++)
        {
            discoveries[i] = new(setup.Routers[i], setup.PeerStores[i], new PubSubDiscoverySettings() { Interval = int.MaxValue }, setup.Peers[i]);
            _ = discoveries[i].DiscoverAsync(setup.Peers[i].Address);
        }

        await Task.Delay(100);

        await setup.Heartbeat();
        await setup.Heartbeat();
        await setup.Heartbeat();

        for (int i = 0; i < setup.Peers.Count; i++)
        {
            discoveries[i].BroadcastPeerInfo();
        }

        foreach (var router in setup.Routers.Values)
        {
            Assert.That(((IRoutingStateContainer)router).ConnectedPeers.Count, Is.EqualTo(totalCount - 1));
        }
    }
}
