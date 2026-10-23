# Remote identities and signed records

Public keys received from peers pass through `PublicKeyDecoder.Decode` before identity
derivation. It returns a fresh protobuf with only `Type` and canonical `Data`:
Ed25519 has exactly 32 bytes, secp256k1 uses compressed SEC1, and RSA/ECDSA use
fully consumed, re-exported SubjectPublicKeyInfo. Unknown key fields do not create new identities.
`PeerId(PublicKey)` uses the same normalization without changing the supplied key.
Raw PeerId byte and string constructors retain their existing representation contracts.

The [peer-ID specification at c750236](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/peer-ids/peer-ids.md#peer-ids)
requires identity multihashes for canonical serialized keys of at most 42 bytes,
including Ed25519 and secp256k1. Larger keys use SHA-256. Inline extraction must
reproduce the entire supplied multihash by canonical derivation; oversized keys,
extra protobuf fields, non-minimal encodings, reordered fields and trailing bytes
cannot establish alternate identities. The independent fixtures in
`IdentityFixtures.cs` check all four key types; the existing decoder tests retain
the Go-derived secp256k1 vectors. Reference behavior and tests are pinned to
[Go's peer implementation](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/core/peer/peer.go)
and [peer tests](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/core/peer/peer_test.go).

Remote RSA keys require a modulus of at least 2048 significant bits. New local
RSA identities use 2048 bits. Existing private-key imports remain readable and
are never regenerated silently. Operators with older, weaker identities must
explicitly replace them to connect to peers enforcing this admission policy.
The floor follows [Go's RSA policy](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/core/crypto/rsa_common.go)
and [remote decoder](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/core/crypto/rsa_go.go).

Pubsub verifies Ed25519, secp256k1, RSA and ECDSA through `Identity.VerifySignature`.
An explicit key must decode and derive the exact `from` PeerId. Empty, malformed
or mismatching explicit keys fail without falling back to the inline key.
Publishing attaches a canonical key after signing when the PeerId cannot carry it.
Verification clones the message and clears both `signature` and `key`, preserving
all other fields, including unknown signed message fields.

There is a discrepancy in the pinned contracts: the
[pubsub prose](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/pubsub/README.md#message-signing)
mentions excluding only `signature`. We follow the interoperable behavior of
[Go v0.14.0](https://github.com/libp2p/go-libp2p-pubsub/blob/3f89e4331c981a6b60206b762a10d015c04875a8/sign.go)
and [Rust v0.56.0](https://github.com/libp2p/rust-libp2p/blob/70082df7e6181722630eabc5de5373733aac9a21/protocols/gossipsub/src/protocol.rs),
which exclude both fields. Key normalization never rewrites signed payloads:
envelope payload/type bytes, Noise static-key input, certificate SubjectPublicKeyInfo input and
WebRTC signaling payloads keep their original signing representation.

Signed peer records retain their original envelope and sequence regardless of
whether they arrive over Identify, Identify Push or pubsub peer exchange. Only
a strictly greater retained sequence can replace a record. A session may restore
its current sequence after eviction when the retained record is absent or older.
[Go's certified address book](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/host/peerstore/pstoremem/addr_book.go#L292-L324)
also rejects older records regardless of source; it permits equal-sequence
refreshes, while this store preserves its existing stricter replacement policy.
Empty records retain their sequence without removing existing addresses.
Record metadata alone does not protect addresses: unsigned discovery can still
fill or refresh them until a usable signed address set is applied. Previously
applied signed addresses remain protected when a newer empty record arrives.

A record's signature authenticates its contents, not the discovery source.
Stateless records remain in the ordinary bounded eviction tier, cannot replace
configured addresses and cannot displace configured peers or active sessions.
When PX defers a configured peer's signed addresses, an authenticated session
can apply the same signed payload at the retained sequence once. It preserves the
original envelope and rejects older or conflicting equal-sequence addresses.
Eviction may forget a stateless record's history; this change does not add an
unbounded or persistent replay-history store. Notifications run outside the
session and store locks and occur only when accepted addresses change.
