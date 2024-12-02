// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Text;

int totalCount = 7;
IPeer[] peers = new IPeer[totalCount];
PeerStore[] peerStores = new PeerStore[totalCount];
PubsubRouter[] routers = new PubsubRouter[totalCount];


for (int i = 0; i < totalCount; i++)
{
    // But we create a seprate setup for every peer
    ServiceProvider sp = new ServiceCollection()
           .AddSingleton(sp => new TestBuilder(sp).AddAppLayerProtocol<GossipsubProtocol>())
           .AddSingleton<ILoggerFactory>(sp => new TestContextLoggerFactory())
           .AddSingleton<PubsubRouter>()
           .AddSingleton<PeerStore>()
           .AddSingleton<ChannelBus>()
           .AddSingleton(sp => new Settings { LowestDegree = 1, Degree = 2, LazyDegree = 2, HighestDegree = 3 })
           .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
           .BuildServiceProvider();

    IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
    IPeer peer = peers[i] = peerFactory.Create(TestPeers.Identity(i));
    PubsubRouter router = routers[i] = sp.GetService<PubsubRouter>()!;
    PubsubPeerDiscoveryProtocol disc = new(router, peerStores[i] = sp.GetService<PeerStore>()!, new PubsubPeerDiscoverySettings() { Interval = 300 }, peer);

    await peer.StartListenAsync([TestPeers.Multiaddr(i)]);
    _ = router.RunAsync(peer, sp.GetService<Settings>());
    //_ = disc.DiscoverAsync(peer.Address);
}

Console.WriteLine($"Emulate peer exchange with one bootstrap peer");

for (int i = 0; i < routers.Length; i++)
{
    routers[i].GetTopic("test");
}

Console.WriteLine($"Center: {string.Join(",", peers[0].ListenAddresses)}");

for (int i = 1; i < peers.Length; i++)
{
    peerStores[i].Discover(peers[0].ListenAddresses.ToArray());
}

await Task.Delay(10000);

Console.WriteLine("Routers");

for (int i = 0; i < routers.Length; i++)
{
    Console.WriteLine(routers[i].ToString());
}

Console.WriteLine("Stores");

for (int i = 0; i < peerStores.Length; i++)
{
    Console.WriteLine(peerStores[i].ToString());
}


await Task.Delay(5000);

var testTopic = routers[1].GetTopic("test");
var testTopicEnd = routers[totalCount - 1].GetTopic("test");
testTopicEnd.OnMessage += (s) => Console.WriteLine(Encoding.UTF8.GetString(s));

testTopic.Publish(Encoding.UTF8.GetBytes("test"));

for (int i = 0; i < 20; i++)
{
    Console.WriteLine(i * 100);
    await Task.Delay(100);
}

Console.WriteLine("Routers");

for (int i = 0; i < routers.Length; i++)
{
    Console.WriteLine(routers[i].ToString());
}
