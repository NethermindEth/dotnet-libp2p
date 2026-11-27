# Discovering peers

Discovery feeds reachable peer addresses into `IPeerStore`. Other parts of the stack, such as `ILocalPeer.DialAsync(PeerId)` and `PubsubRouter`, then use the store to connect to known peers.

## Manual discovery

Manual discovery is useful for bootstrapping, tests, and static peer lists. With a configured service provider and local peer:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

IPeerStore peerStore = provider.GetRequiredService<IPeerStore>();

Multiaddress remoteAddress = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001/p2p/12D3KooW...");
peerStore.Discover([remoteAddress]);

PeerId remotePeerId = remoteAddress.GetPeerId()!;
ISession session = await localPeer.DialAsync(remotePeerId);
```

Every discovered address should include `/p2p/<peer-id>`. Addresses without a peer id cannot be stored as a reachable peer.

Subscribe to receive retained addresses immediately, followed by new or changed address sets. Discovery can run during replay, so a callback must tolerate duplicate and concurrent notifications.

```csharp
peerStore.OnNewPeer += addrs =>
{
    Console.WriteLine($"Discovered {string.Join(", ", addrs)}");
};
```

## Peer store

Use `TryGetPeerInfo` to read addresses, protocols, the agent version, and the verified signed record associated with a peer. A miss returns `false` and leaves the store unchanged.

```csharp
if (peerStore.TryGetPeerInfo(remotePeerId, out PeerMetadata? metadata))
{
    Console.WriteLine(metadata.AgentVersion);
}
```

The metadata and its lists are read-only snapshots. Treat the shared `Multiaddress` objects as read-only too.

`AddLibp2p` registers a shared in-memory `PeerStore` as `IPeerStore`. Register your own singleton on the same service collection before calling `AddLibp2p` to use it across the stack.

```csharp
var services = new ServiceCollection();
services.AddSingleton<IPeerStore>(myPeerStore);
services.AddLibp2p();
```

When using `AddLibp2p`, register the store on the host service collection. Building the factory rejects a store registration in `builder.Services` if the host already supplies one. With a standalone `Libp2pPeerFactoryBuilder`, register the singleton on `builder.Services` before calling `Build`.

Custom stores must preserve the discovery trust and signed-record rules, retain entries for active sessions, and replay addresses to new subscribers. A durable store can load its retained metadata before discovery starts and satisfy synchronous reads from memory. Active session retention belongs to the current process.

## mDNS discovery

`AddLibp2p` registers `MDnsDiscoveryProtocol`. Start it after `StartListenAsync`, when the local peer has real listen addresses to advertise:

```csharp
using Nethermind.Libp2p.Protocols;

await localPeer.StartListenAsync();

MDnsDiscoveryProtocol mdns = provider.GetRequiredService<MDnsDiscoveryProtocol>();
await mdns.StartDiscoveryAsync(localPeer.ListenAddresses, cancellationToken);
```

mDNS advertises the local addresses on the local network and calls `IPeerStore.Discover(..., isTrusted: false)` when it sees other peers. It is intended for LAN discovery; it does not replace bootstrap peers or a DHT on wider networks.

## Pubsub peer discovery

Pubsub peer discovery advertises public keys and addresses over pubsub topics. It needs pubsub enabled and the pubsub router started:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.PubsubPeerDiscovery;

ServiceProvider provider = new ServiceCollection()
    .AddPubsub()
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
    provider.GetRequiredService<IPeerStore>(),
    provider.GetRequiredService<PubsubPeerDiscoverySettings>(),
    localPeer);

await discovery.StartDiscoveryAsync(localPeer.ListenAddresses, cancellationToken);
```

All peers that should discover each other must join at least one common discovery topic. When a peer announcement is received, the protocol decodes the advertised addresses and passes them to `IPeerStore` as untrusted discovery.

## Stopping discovery

Discovery stops when the token passed to `StartDiscoveryAsync` is cancelled or when the discovery protocol is disposed. `DisposeAsync` also waits for the background loop to finish:

- `MDnsDiscoveryProtocol` stops querying, sends an mDNS goodbye for the advertised service and releases its sockets.
- `PubsubPeerDiscoveryProtocol` stops broadcasting, removes its topic handlers and unsubscribes from the discovery topics.

The peer store, pubsub router, and local peer have their own lifetimes.

`AddLibp2p()` registers the mDNS discovery service and peer store. `AddPubsub()` registers the pubsub router. The service provider disposes the instances it creates. Dispose instances you create with `new`, such as `PubsubPeerDiscoveryProtocol` in the example above, yourself:

```csharp
await discovery.DisposeAsync();
await provider.DisposeAsync(); // disposes the mDNS discovery and the pubsub router
```

A disposed discovery protocol cannot be started again.
