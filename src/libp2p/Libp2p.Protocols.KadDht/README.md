# Libp2p.Protocols.KadDht

Kademlia DHT support for dotnet-libp2p. It implements the libp2p [`/ipfs/kad/1.0.0`](https://github.com/libp2p/specs/blob/master/kad-dht/README.md) protocol and integrates it into an `ILibp2pPeerFactoryBuilder`.

## Install

```sh
dotnet add package Libp2p.Protocols.KadDht
```

## Configure

Register the DHT while configuring libp2p. Server mode is the default; client mode participates in routing without serving stored records.

```csharp
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;

var services = new ServiceCollection();

services.AddLibp2p(builder => builder.AddKadDht(options =>
{
    options.Mode = KadDhtMode.Server;
    options.KSize = 20;
    options.Alpha = 10;
}));
```

After the local peer is initialized, call `RunKadDhtAsync` to bootstrap the DHT and run its maintenance loop. Use `GetKadDht` to retrieve the configured `KadDhtProtocol` for DHT operations.

## Configuration

`KadDhtOptions` controls the routing-table size and query concurrency (`KSize` and `Alpha`), operating mode, record and provider retention, storage limits, message limits, maintenance intervals, operation timeout, and disjoint lookup paths. The default mode is `KadDhtMode.Server`.

## License

LGPL-3.0-only.
