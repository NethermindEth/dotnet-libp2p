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

The default in-memory provider store retains at most `MaxProviderKeys` (10,000) keys,
`MaxProvidersPerKey` (20) providers per key, and inspects the first
`MaxAddressesPerProvider` (16) addresses per record. `MaxProviderRecordBytes` (1,024)
bounds each record's variable-length payload: twice the Base64 key length, plus the
peer ID byte length, twice its Base58 dictionary-key length, and twice the sum of
retained address-string lengths. The shared key is conservatively charged to every
record. Object and collection overhead and transient message parsing are additional.

Records whose key and identity alone exceed that budget are rejected. Whole addresses
that do not fit are omitted, preserving the order of those retained; malformed or
skipped wire entries still consume an address slot. Zero retained addresses is allowed.
The store owns a read-only address-list snapshot. Incoming announcements apply the same
payload policy before calling a custom store; custom stores own their aggregate limits.
Operators may raise these positive limits explicitly.

At key capacity, new keys are rejected while all buckets contain live records. Existing
providers can still refresh, and expired providers on the requested key are pruned before
admission. A fully expired unrelated bucket is reclaimed when needed, even without
`RunAsync` or intervening reads. Maintenance and lookups also remove expired records;
expiration occurs strictly after `StoredAt + Ttl`. Live records are never evicted to admit
a new key. These are local resource limits and do not change the wire protocol.

Bucket refresh uses approximate random raw-key sampling. A FIND_NODE receiver hashes the raw key, so selecting raw bytes that hash into a particular bucket prefix would require finding bytes with a chosen SHA-256 prefix. This limitation replaces the former non-wire-compatible `PublicKey.FromHash` shortcut.

## Migrating from the in-tree Kademlia implementation

This migration intentionally changes the public API. Generic routing and lookup types now come from `Nethermind.Kademlia`. `ValueHash256.Bytes` is a `ReadOnlySpan<byte>`; use `ValueHash256.FromBytes(...)` to construct a hash and `hash.Bytes.ToArray()` when an owned array is needed. The mutable `Bytes` setter and `BytesAsSpan` accessor are no longer available. Consumers must rebuild against the new package API.

## License

This project is MIT-licensed. The separate `Nethermind.Kademlia` package has its own license metadata.
