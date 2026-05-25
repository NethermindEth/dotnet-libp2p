# Libp2p.Protocols.NatTraversal

NAT traversal coordination for libp2p peers.

## Features

- DCUtR (`/libp2p/dcutr`) hole-punch coordination
- CONNECT/SYNC message exchange
- Observed-address exchange using libp2p-compatible protobuf wire encoding

## Usage

```csharp
using Nethermind.Libp2p.Protocols.NatTraversal;
using Nethermind.Libp2p.Protocols.NatTraversal.Extensions;

builder.AddNatHolePunch();

HolePunchResult result = await session.DialAsync<NatHolePunchProtocol, HolePunchRequest, HolePunchResult>(
    new HolePunchRequest(observedAddresses),
    token);
```

## License

MIT
