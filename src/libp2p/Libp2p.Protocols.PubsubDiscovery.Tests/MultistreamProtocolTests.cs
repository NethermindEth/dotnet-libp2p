// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.PubSubDiscovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Libp2p.Protocols.Multistream.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class MultistreamProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake2()
    {
        IPeerFactory peerFactory = new TestBuilder().Build();
        ChannelBus commonBus = new();

        ServiceProvider sp1 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton<IPeerFactory>(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
             .BuildServiceProvider();


        ServiceProvider sp2 = new ServiceCollection()
             .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
             .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
             .AddSingleton<PubsubRouter>()
             .AddSingleton<PeerStore>()
             .AddSingleton<IPeerFactory>(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
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
        // There is common communication point
        ChannelBus commonBus = new();
        ILocalPeer[] peers = new ILocalPeer[10];
        PeerStore[] peerStores = new PeerStore[10];
        PubsubRouter[] routers = new PubsubRouter[10];


        for (int i = 0; i < peers.Length; i++)
        {
            // But we create a seprate setup for every peer
            ServiceProvider sp = new ServiceCollection()
                   .AddSingleton(sp => new TestBuilder(commonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
                   .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton<PeerStore>()
                   .AddSingleton(sp=> sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
            ILocalPeer peer = peers[i] = peerFactory.Create(TestPeers.Identity(i));
            PubsubRouter router = routers[i] = sp.GetService<PubsubRouter>()!;
            PeerStore peerStore = sp.GetService<PeerStore>()!;
            PubSubDiscoveryProtocol disc = new(router, new PubSubDiscoverySettings() { Interval = 300 }, peerStore, peer);
            _ = router.RunAsync(peer, peerStore);
            peerStores[i] = peerStore;
            _ = disc.DiscoverAsync(peers[i].Address);
        }

        for (int i = 0; i < peers.Length; i++)
        {
            peerStores[i].Discover([peers[(i + 1) % 10].Address]);
        }
        await Task.Delay(1500000);
        await Task.Delay(2000);
        //IChannel downChannel = new TestChannel();
        //IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        //IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        //IPeerContext peerContext = Substitute.For<IPeerContext>();
        //peerContext.SpecificProtocolRequest.Returns((IChannelRequest?)null);

        //IProtocol? proto1 = Substitute.For<IProtocol>();
        //proto1.Id.Returns("proto1");
        //channelFactory.SubProtocols.Returns(new[] { proto1 });
        //IChannel upChannel = new TestChannel();
        //channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
        //    .Returns(Task.CompletedTask);

        //MultistreamProtocol proto = new();
        //Task dialTask = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        //_ = Task.Run(async () =>
        //{
        //    await downChannel.WriteLineAsync(proto.Id);
        //    await downChannel.WriteLineAsync("proto1");
        //});

        //Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        //Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));

        //await dialTask;

        //_ = channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        //await downChannel.CloseAsync();
    }


}
