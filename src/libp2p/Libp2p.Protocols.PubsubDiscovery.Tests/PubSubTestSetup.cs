using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Protocols.PubsubDiscovery.Tests;

class PubSubTestSetup
{
    static TestContextLoggerFactory fac = new TestContextLoggerFactory();

    public ChannelBus CommonBus { get; } = new(fac);
    public Dictionary<int, ILocalPeer> Peers { get; } = new();
    public Dictionary<int, PeerStore> PeerStores { get; } = new();
    public Dictionary<int, PubsubRouter> Routers { get; } = new();

    public void Add(int count)
    {
        int initialCount = Peers.Count;
        // There is common communication point

        for (int i = initialCount; i < initialCount + count; i++)
        {
            // But we create a seprate setup for every peer
            Settings settings = new Settings
            {
                HeartbeatInterval = int.MaxValue,
            };

            ServiceProvider sp = new ServiceCollection()
                   .AddSingleton(sp => new TestBuilder(CommonBus, sp).AddAppLayerProtocol<GossipsubProtocol>())
                   .AddSingleton((Func<IServiceProvider, ILoggerFactory>)(sp => fac))
                   .AddSingleton<PubsubRouter>()
                   .AddSingleton(settings)
                   .AddSingleton<PeerStore>()
                   .AddSingleton(sp => sp.GetService<IPeerFactoryBuilder>()!.Build())
                   .BuildServiceProvider();

            IPeerFactory peerFactory = sp.GetService<IPeerFactory>()!;
            ILocalPeer peer = Peers[i] = peerFactory.Create(TestPeers.Identity(i));
            PubsubRouter router = Routers[i] = sp.GetService<PubsubRouter>()!;
            PeerStore peerStore = sp.GetService<PeerStore>()!;
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
