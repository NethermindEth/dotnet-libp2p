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

Wire peer responses have separate positive limits: `MaxPeerIdBytes` (128 bytes),
`MaxAddressesPerPeer` (16 closer-peer address slots), `MaxPeerRecordBytes` (8 KiB
per peer), and `MaxPeerResponseBytes` (64 KiB across both peer lists). The first
`KSize` closer entries and `MaxProvidersPerKey` provider entries are inspected;
provider addresses use `MaxAddressesPerProvider`. Invalid, duplicate, and skipped
entries consume slots. Closer peers take priority over providers in the combined
budget. Encoded bytes (including protobuf peer-field framing) and retained payload
are bounded independently. Retained payload charges both identity copies and UTF-16
address strings, including destination IDs appended during normalization. Admitted
address parsing consumes budget even on failure; oversized whole addresses are
skipped. These response limits also apply to custom stores and nearest-peer callbacks
and do not replace the provider store's retention limits above.

FIND_NODE also counts requester entries toward the inspected prefix before excluding
them from the response. This can return fewer than `KSize` peers; filtering first
would permit unbounded enumeration of a custom nearest-peer callback containing
repeated requester entries.

Wire identities must be complete multihashes with minimal unsigned varint headers of at
most nine bytes and an exact digest length; unknown hash codes remain structurally
acceptable. Response addresses bound to another destination are omitted. Received
closer peers without usable addresses are excluded from lookup and discovery;
provider records may still have no addresses. Protobuf parsing is separately bounded
by `MaxMessageSize`; response budgets bound subsequent conversion and retention.
These limits bound one response, rather than cumulative discovery over many requests.

The public `MessageHelper.ToWirePeer` and `CreateAddProviderRequest` helpers throw
`ArgumentException` for malformed peer IDs or IDs exceeding the default 128-byte
limit. Response builders omit invalid entries instead. Callers using the public
helpers should validate their inputs or handle that exception.

PUT_VALUE echoes a record only when the store reports that its bytes were stored.
The envelope and record keys must match. Channel closure cancels cancellation-aware
storage operations, and cancelled operations produce no acknowledgement. Connection
flags default to `NOT_CONNECTED`; registered response handlers report `CONNECTED`
only for a matching successfully established session whose closure token is live.
This is evidence at serialization time, rather than a guarantee of future reachability.

Wire semantics follow the [Kad-DHT specification](https://github.com/libp2p/specs/blob/89582889fa291d572c11723b3c0c6b2a964947aa/kad-dht/README.md#protocol-messages)
and [multiformats unsigned varint encoding](https://github.com/multiformats/unsigned-varint/blob/dd184f1ce89c81808503bb719b5fcb13844d08f3/README.md#practical-maximum-of-9-bytes-for-security).
The conservative connection mapping also matches [go-libp2p](https://github.com/libp2p/go-libp2p-kad-dht/blob/c5e0ce5abb0b63c4c47426bfae910fcf73e2a265/pb/message.go#L205).

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

## Builder and compatibility migration

Automatic protocol activation now happens at `Build()`, after builder `Services`
registrations are complete. Supplied services remain available during configuration;
new builder registrations resolve at build time, including nested constructor and factory
dependencies. Builder registrations take precedence over the supplied provider. The first
successful build freezes registrations and subsequent builds return the same factory.
Late service or protocol changes throw `InvalidOperationException`. Dispose the builder
(or the containing DI provider) after its peers; it disposes its own services and
implicitly created protocols while leaving explicit instances and supplied services owned
by their original registration. A failed build can be corrected and retried.
Factories for different registrations may run concurrently. Factory calls and supplied
provider resolution run outside the container cache lock. Disposal rejects new resolutions
and waits for active resolutions before disposing owned services; concurrent disposal
calls await the same cleanup.

`AddKadDht`, `AddKadDht` on `IServiceCollection`, and `WithKadDht` share the same
request handler. The standard builder creates routing state and runtime identity lazily
once per peer; it does not require an `ILocalPeer` singleton. `GetKadDht()`,
`GetProtocol<KadDhtProtocol>()`, and `RunKadDhtAsync()` still work. The runtime is
not a second negotiated listener. Client mode keeps outbound dialing available while
refusing inbound DHT negotiation and omitting DHT from Identify. The additive
`ProtocolRef.AcceptInbound` flag controls listener negotiation separately from
`IsExposed`; other protocol registrations keep their previous defaults. Custom builders can implement the additive
`IPeerServiceBuilder` and `IProtocolFactoryBuilder` capabilities. Existing builder
interfaces have no new mandatory members. Handler-only custom builders remain supported.

Retrieve a client's peer-bound service with
`((IPeerServices)peer).GetPeerService<DhtClient>()`. The existing singleton-peer DI
pattern also retains `GetRequiredService<KadDhtProtocol>()` and now registers `DhtClient`.
Explicit store arguments override registered stores; registered stores override defaults.
Store instances retain their configured capacities and existing DI sharing semantics,
while each peer has its own routing state. Use a separate provider per peer when stores
must also be isolated.

`DhtClient` retains its original constructor and string methods and adds a constructor
accepting `IDhtMessageSender`, `IValueStore`, `IRecordValidator`, and `KadDhtOptions`.
The old constructor is obsolete. `DhtClient` now implements `IDisposable` and `IAsyncDisposable`; peer disposal
cancels and drains its active operations. Dispose manually constructed clients after use; prefer asynchronous disposal when operations are active.
Both paths validate PUTs and cached/remote GET results,
select valid remote candidates through the validator, and propagate caller cancellation.
Rejected local writes do not count as storage successes. A valid lookup can return a
value even when cache capacity prevents storing it. Default production validation uses
`CompositeRecordValidator`, including signed IPNS validation. GET responses must carry
the requested record key. Local and wire reads revalidate custom or legacy store content.
These intentional behavior changes reject records previously accepted without validation.
For binary keys and values (including IPNS), use the byte-oriented `KadDhtProtocol` API;
the legacy string client continues to encode and decode UTF-8.

Public API inspection found exported signatures for both `TestNode` types,
`TestNodeAddressBook`, `PublicKeyKeyOperator`, `KademliaSessionManager`, `ISessionManager`,
`SessionOptions`, the generic network sender, and the legacy value store. Absence of
in-tree consumers does not establish external usage. No public type, constructor, or
method was removed or internalized. Both `TestNode` types, the address book, key operator,
session manager, generic sender, old client constructor, and `SharedDhtState.ValueStore`
are deprecated with `Obsolete` warnings. Existing consumers using warnings-as-errors must
migrate or acknowledge those warnings specifically at their legacy call sites.
Use `DhtNode`, `KadDhtProtocol`, `IDhtMessageSender`, and `IValueStore` for new code.
The compatibility session manager adapts legacy nodes at its boundary and runs the
routing algorithm on `DhtNode`; production DI does not register TestNode-based services.
The internal test transport helper lives in the test assembly.

`SharedDhtState.ValueStore` is a synchronous compatibility facade over the authoritative
`IValueStore`, with no second dictionary or cleanup timer. Standalone `DhtValueStore`
construction remains supported; reads filter expiration, and explicit store cleanup
reclaims expired entries. The facade does not own or dispose its backing store. Production
operations use the asynchronous store directly. There is no wire ID or protobuf change.
The `DhtClient` constructor accepting an `IValueStore` binds the supplied state's
compatibility facade to that store and the configured record TTL. Manually constructed
clients sharing one `SharedDhtState` must use the same store and TTL; use separate state
objects for different stores. Production registration creates one client per peer-bound state.
