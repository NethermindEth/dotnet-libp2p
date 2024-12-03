using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Tests;

class PubsubTestSetup
{
    static TestContextLoggerFactory fac = new();

    public ChannelBus CommonBus { get; } = new(fac);
    public Dictionary<int, IPeer> Peers { get; } = new();
    public Dictionary<int, PeerStore> PeerStores { get; } = new();
    public Dictionary<int, PubsubRouter> Routers { get; } = new();

    public async Task AddAsync(int count)
    {
        int initialCount = Peers.Count;
        // There is common communication point

        for (int i = initialCount; i < initialCount + count; i++)
        {
            // But we create a seprate setup for every peer
            Settings settings = new()
            {
                HeartbeatInterval = int.MaxValue,
            };

            ServiceProvider sp = new ServiceCollection()
                   .AddSingleton(sp => new TestBuilder(sp).AddAppLayerProtocol<GossipsubProtocol>())
                   .AddSingleton((Func<IServiceProvider, ILoggerFactory>)(sp => fac))
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton(settings)
                   .AddSingleton(CommonBus)
                   .AddSingleton<PeerStore>()
                   .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
            IPeer peer = Peers[i] = peerFactory.Create(TestPeers.Identity(i));
            PubsubRouter router = Routers[i] = sp.GetService<PubsubRouter>()!;
            PeerStore peerStore = sp.GetService<PeerStore>()!;
            await peer.StartListenAsync([TestPeers.Multiaddr(i)]);
            _ = router.RunAsync(peer);
            PeerStores[i] = peerStore;
        }
    }

    internal async Task Heartbeat()
    {
        foreach (PubsubRouter router in Routers.Values)
        {
            await router.Heartbeat();
        }
    }
}
