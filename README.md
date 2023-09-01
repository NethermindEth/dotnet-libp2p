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

As an application developer, you may consider reading [quick start](./docs/quick-start.md).
As a stack implementer, you may be interested in [more advanced tutorials](./docs/development/README.md).
**Contributors are welcomed**, kindly check the issues tab, everything there if not assigned to a person can be taken into work. More details in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Roadmap

From the beginning, the target is to provide a performant well-tested implementation that works on multiple platforms. With high throughput and low memory profile. The modules to be implemented firstly should cover basic P2P application needs.

| Protocol           | Version            | Status          |
|--------------------|--------------------|-----------------|
| **Transports**
| TCP                | tcp                | ✅             |
| QUIC               | quic               | ⬜ help wanted |
|                    | quic-v1            | ⬜ help wanted |
| **Protocols**
| multistream-select | /multistream/1.0.0 | ✅             |
| plaintext          | /plaintext/2.0.0   | ✅             |
| noise              | /noise             | ✅             |
| mplex?             | /mplex/6.7.0       | ⬜             |
| yamux              | /yamux/1.0.0       | ✅             |
| Identify           | /ipfs/id/1.0.0     | ✅             |
| ping               | /ipfs/ping/1.0.0   | 🚧             |
| pubsub             | /floodsub/1.0.0    | ✅             |
|                    | /meshsub/1.0.0     | 🚧             |
|                    | /meshsub/1.1.0     | 🚧             |
|                    | /meshsub/1.2.0     | ⬜             |
| Circuit Relay      |                    | ⬜ help wanted |
| **Discovery**
| mDns               | basic              | ✅             |
|                    | DNS-SD             | ⬜             |
| discv5             | 5.1                | ⬜ help wanted |

⬜ - not yet implemented<br>
🚧 - work in progress<br>
✅ - basic support implemented

## License

dotnet-libp2p is an open-source software licensed under the [MIT](https://github.com/nethermindeth/dotnet-libp2p/blob/main/LICENSE).
