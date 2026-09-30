# Kad-DHT

Kademlia DHT support for dotnet-libp2p. It implements the libp2p [`/ipfs/kad/1.0.0`](https://github.com/libp2p/specs/blob/master/kad-dht/README.md) protocol and integrates it into an `ILibp2pPeerFactoryBuilder`.

The separate `Nethermind.Kademlia` 2.0.0 package provides the generic routing table, peer health, and iterative lookup algorithms. This package provides the libp2p protocol, peer and key adapters, network message sender, record stores, and validation.

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

The default protocol ID is `/ipfs/kad/1.0.0`; `KadDhtOptions.ProtocolId` can select another network's protocol ID. A single protobuf `Message` envelope carries PING, FIND_NODE, GET_VALUE, PUT_VALUE, GET_PROVIDERS, and ADD_PROVIDER.

FIND_NODE sends raw target-key bytes; the receiver hashes those bytes with SHA-256 once for distance calculations. The sender and local routing table use the same rule. Relayed multiaddresses retain the destination peer ID after `/p2p-circuit` rather than treating the relay as the destination.

ADD_PROVIDER is a one-way wire operation. The sender does not wait for a response, but dialing and writing failures still propagate. PUT_VALUE and GET_VALUE use request/response messages. The higher-level `DhtClient` replicates to and queries the nearest peers currently known to its local routing table; it does not perform an additional iterative network search.

Bucket refresh uses approximate random raw-key sampling. A FIND_NODE receiver hashes the raw key, so selecting raw bytes that hash into a particular bucket prefix would require finding bytes with a chosen SHA-256 prefix. This limitation replaces the former non-wire-compatible `PublicKey.FromHash` shortcut.

## Migrating from the in-tree Kademlia implementation

This migration intentionally changes the public API. Generic routing and lookup types now come from `Nethermind.Kademlia`. `ValueHash256.Bytes` is a `ReadOnlySpan<byte>`; use `ValueHash256.FromBytes(...)` to construct a hash and `hash.Bytes.ToArray()` when an owned array is needed. The mutable `Bytes` setter and `BytesAsSpan` accessor are no longer available. Consumers must rebuild against the new package API.

## License

This project is MIT-licensed. The separate `Nethermind.Kademlia` package has its own license metadata.
