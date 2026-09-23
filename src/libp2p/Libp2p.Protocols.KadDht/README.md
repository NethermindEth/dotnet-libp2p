# Kad-DHT

This project connects .NET libp2p to the `Nethermind.Kademlia` 2.0.0 package. The package owns the generic routing table, peer health, and iterative lookup algorithms. This repository owns the libp2p protocol, peer and key adapters, network message sender, record stores, and validation.

The DHT uses `/ipfs/kad/1.0.0` by default. `KadDhtOptions.ProtocolId` can select another protocol ID when joining a network that uses one. A single protobuf `Message` envelope carries PING, FIND_NODE, GET_VALUE, PUT_VALUE, GET_PROVIDERS, and ADD_PROVIDER.

## Integration

Register services with `services.AddKadDht(options => { ... })` and add the protocol handlers to a peer factory with `builder.WithKadDht()`. `KadDhtOptions` controls the protocol ID, bucket size (`KSize`), query concurrency (`Alpha`), operating mode, record limits, and maintenance intervals. `KadDhtProtocol` exposes value storage and retrieval, provider announcement and discovery, bootstrap, and maintenance methods.

The libp2p-specific pieces are:

- `KadDhtProtocol`: public DHT operations and protocol lifecycle.
- `SharedDhtState`: access to the package routing table and local value store.
- `LibP2pKademliaMessageSender`: wire requests, session reuse, and discovered-peer addresses.
- `DhtKeyOperator` and `DhtNodeHashProvider`: map raw libp2p keys and peer IDs to Kademlia hashes.
- `IValueStore`, `IProviderStore`, and `IRecordValidator`: record storage and validation.

FIND_NODE sends raw target-key bytes; the receiver hashes those bytes with SHA-256 once for distance calculations. The sender and local routing table use the same rule. Relayed multiaddresses retain the destination peer ID after `/p2p-circuit` rather than treating the relay as the destination.

ADD_PROVIDER is a one-way wire operation. The sender does not wait for a response, but dialing and writing failures still propagate. PUT_VALUE and GET_VALUE use request/response messages. The higher-level `DhtClient` replicates to and queries the nearest peers currently known to its local routing table; it does not perform an additional iterative network search.

Bucket refresh uses approximate random raw-key sampling. A FIND_NODE receiver hashes the raw key, so selecting raw bytes that hash into a particular bucket prefix would require finding bytes with a chosen SHA-256 prefix. This limitation replaces the former non-wire-compatible `PublicKey.FromHash` shortcut.

## License and tests

This project is MIT-licensed. The separate `Nethermind.Kademlia` NuGet package has its own license metadata. Kad-DHT tests cover routing adapters, wire-key handling, provider announcements, record stores, protocol behavior, and bootstrap recovery; the repository's CI workflow runs the suite.
