# Libp2p.Protocols.NatTraversal

NAT traversal module for libp2p enabling peer-to-peer connectivity in NAT-restricted networks.

## Features

- STUN client for public IP/port discovery (RFC 5389)
- TURN allocation request scaffolding for relay fallback (RFC 5766)
- NAT type model for later detection support

## Installation

```bash
dotnet add package Nethermind.Libp2p.Protocols.NatTraversal
```

## Usage

```csharp
using Nethermind.Libp2p.Protocols.NatTraversal;

var stunClient = new StunClient();
StunResult result = await stunClient.DiscoverAsync(stunServers, token);
```

## STUN Servers

Default STUN servers used:
- stun.l.google.com:19302
- stun1.l.google.com:19302
- stun2.l.google.com:19302

## License

MIT
