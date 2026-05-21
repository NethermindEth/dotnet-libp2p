# Discovering peers

Discovery feeds reachable peer addresses into `PeerStore`. Other parts of the stack, such as `ILocalPeer.DialAsync(PeerId)` and `PubsubRouter`, then use the store to connect to known peers.

## Manual discovery

Manual discovery is useful for bootstrapping, tests, and static peer lists. With a configured service provider and local peer:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

PeerStore peerStore = provider.GetRequiredService<PeerStore>();

Multiaddress remoteAddress = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001/p2p/12D3KooW...");
peerStore.Discover([remoteAddress]);

PeerId remotePeerId = remoteAddress.GetPeerId()!;
ISession session = await localPeer.DialAsync(remotePeerId);
```

Every discovered address should include `/p2p/<peer-id>`. Addresses without a peer id cannot be stored as a reachable peer.

You can also subscribe to newly discovered peers:

```csharp
peerStore.OnNewPeer += addrs =>
{
    Console.WriteLine($"Discovered {string.Join(", ", addrs)}");
};
```

## mDNS discovery

`AddLibp2p` registers `MDnsDiscoveryProtocol`. Start it after `StartListenAsync`, when the local peer has real listen addresses to advertise:

```csharp
using Nethermind.Libp2p.Protocols;

await localPeer.StartListenAsync();

MDnsDiscoveryProtocol mdns = provider.GetRequiredService<MDnsDiscoveryProtocol>();
await mdns.StartDiscoveryAsync(localPeer.ListenAddresses, cancellationToken);
```

mDNS advertises the local addresses on the local network and calls `PeerStore.Discover(...)` when it sees other peers. It is intended for LAN discovery; it does not replace bootstrap peers or a DHT on wider networks.

## Pubsub peer discovery

Pubsub peer discovery exchanges signed peer records over pubsub topics. It needs pubsub enabled and the pubsub router started:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;

ServiceProvider provider = new ServiceCollection()
    .AddSingleton(new PubsubPeerDiscoverySettings
    {
        Topics = ["_peer-discovery._p2p._pubsub"],
        Interval = 10_000,
    })
    .AddLibp2p(builder => builder.WithPubsub())
    .BuildServiceProvider();

ILocalPeer localPeer = provider.GetRequiredService<IPeerFactory>().Create();
await localPeer.StartListenAsync();

PubsubRouter router = provider.GetRequiredService<PubsubRouter>();
await router.StartAsync(localPeer);

var discovery = new PubsubPeerDiscoveryProtocol(
    router,
    provider.GetRequiredService<PeerStore>(),
    provider.GetRequiredService<PubsubPeerDiscoverySettings>(),
    localPeer);

await discovery.StartDiscoveryAsync(localPeer.ListenAddresses, cancellationToken);
```

All peers that should discover each other must join at least one common discovery topic. When a peer announcement is received, the protocol decodes the advertised addresses and stores them in `PeerStore`.
