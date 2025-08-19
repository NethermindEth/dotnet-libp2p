using Libp2p.E2eTests;
using Libp2p.Protocols.Pubsub.E2eTests;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;

namespace Libp2p.Protocols.PubsubPeerDiscovery.E2eTests;

public class PubsubDiscoveryE2eTestSetup : PubsubE2eTestSetup
{
    public PubsubPeerDiscoverySettings DefaultDiscoverySettings { get; set; } = new PubsubPeerDiscoverySettings { Interval = 300 };

    public Dictionary<int, PubsubPeerDiscoveryProtocol> Discovery { get; } = [];

    protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
    {
        return base.ConfigureLibp2p(builder)
            .AddProtocol<IncrementNumberTestProtocol>();
    }

    protected override IServiceCollection ConfigureServices(IServiceCollection col)
    {
        return base.ConfigureServices(col)
            .AddSingleton(new PubsubPeerDiscoverySettings())
            .AddSingleton<PubsubPeerDiscoveryProtocol>();
    }

    protected override void AddAt(int index)
    {
        base.AddAt(index);
        Discovery[index] = new PubsubPeerDiscoveryProtocol(Routers[index], PeerStores[index], DefaultDiscoverySettings, Peers[index], loggerFactory);

        _ = Discovery[index].StartDiscoveryAsync(Peers[index].ListenAddresses, Token);
    }
}
