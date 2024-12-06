using Libp2p.Protocols.Pubsub.E2eTests;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Protocols.PubsubPeerDiscovery.Tests;

public static class PubsubTestSetupExtensions
{
    public static void AddPubsubPeerDiscovery(this PubsubTestSetup self, bool start = true)
    {
        foreach ((int index, PubsubRouter router) in self.Routers)
        {
            PubsubPeerDiscoveryProtocol disc = new(router, self.PeerStores[index], new PubsubPeerDiscoverySettings() { Interval = 300 }, self.Peers[index]);
            if (start) _ = disc.DiscoverAsync(self.Peers[index].ListenAddresses);
        }
    }
}
