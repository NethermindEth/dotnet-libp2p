<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://github.com/libp2p/libp2p/blob/master/logo/white-bg-2.png?raw=true">
    <source media="(prefers-color-scheme: light)" srcset="https://github.com/libp2p/libp2p/blob/master/logo/black-bg-2.png?raw=true">
    <img alt="libp2p" src="https://github.com/libp2p/libp2p/blob/master/logo/black-bg-2.png?raw=true" height="96">
  </picture>
</p>

# dotnet-libp2p

[![Test](https://github.com/nethermindeth/dotnet-libp2p/actions/workflows/test.yml/badge.svg)](https://github.com/nethermindeth/dotnet-libp2p/actions/workflows/test.yml)
[![Nethermind.Libp2p](https://img.shields.io/nuget/v/Nethermind.Libp2p)](https://www.nuget.org/packages/Nethermind.Libp2p)

The project aims to implement [libp2p](https://libp2p.io) to unlock building .NET peer-to-peer applications using a battle-tested specification of network communication of the new age.

As an application developer, you may consider reading [quick start](./docs/README.md).
As a stack implementer, you may be interested in [more advanced tutorials](./docs/development/README.md).

**Contributions are welcome**, kindly check the [issues](https://github.com/NethermindEth/dotnet-libp2p/issues) tab, everything there if not assigned to a person can be taken into work. More details in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Building the solution

The solution depends on external repositories.

```sh
git clone https://github.com/NethermindEth/dotnet-libp2p.git --recursive
cd ./src/libp2p/
dotnet build
dotnet test
```

## Roadmap

🚧 The library is not stable and under heavy development. Consider the [beta](https://github.com/NethermindEth/dotnet-libp2p/milestone/5) milestone as a reflection of readiness for production 🚧

The target is to provide a performant well-tested implementation of a wide range of protocols that works on multiple platforms, with high throughput and low memory profile.


| Protocol           | Version            | Status          |
|--------------------|--------------------|-----------------|
| TCP                | tcp                | ✅             |
| QUIC               | quic-v1            | ✅             |
| multistream-select | /multistream/1.0.0 | ✅             |
| plaintext          | /plaintext/2.0.0   | ✅             |
| noise              | /noise             | ✅             |
| tls                | /tls/1.0.0         | ⬜ help wanted |
| WebTransport       |                    | ⬜ help wanted |
| yamux              | /yamux/1.0.0       | ✅             |
| Circuit Relay      | /libp2p/circuit/relay/0.2.0/* | ⬜ help wanted |
| hole punching      |                    | ⬜ help wanted |
| **Application layer**
| Identify           | /ipfs/id/1.0.0     | ✅             |
| ping               | /ipfs/ping/1.0.0   | ✅             |
| pubsub             | /floodsub/1.0.0    | ✅             |
|                    | /meshsub/1.0.0     | ✅             |
|                    | /meshsub/1.1.0     | 🚧             |
|                    | /meshsub/1.2.0     | ⬜             |
| **Discovery**
| mDns               | basic              | ✅             |
|                    | DNS-SD             | 🚧             |
| [discv5](https://github.com/Pier-Two/Lantern.Discv5) | 5.1 | 🚧 help wanted |

⬜ - not yet implemented<br>
🚧 - work in progress<br>
✅ - basic support implemented

No plans for: mplex, quic(draft-29)

## License

dotnet-libp2p is an open-source software licensed under the [MIT](https://github.com/nethermindeth/dotnet-libp2p/blob/main/LICENSE).
