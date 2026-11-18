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
[![.NET libp2p](https://img.shields.io/badge/telegram-.NET%20libp2p-blue?logo=telegram)](https://t.me/dotnet_libp2p)
[![Discord](https://img.shields.io/discord/1204447718093750272?style=flat&logo=discord")](https://discord.com/channels/1204447718093750272/1341468555568353330)

The project aims to implement [libp2p](https://libp2p.io) to unlock building .NET peer-to-peer applications using a battle-tested specification of network communication of the new age.

The docs from the application developer perspective: [quick start](./docs/README.md), [sessions](./docs/sessions.md), [discovery](./docs/discovery.md), and [pubsub](./libp2p/Libp2p.Protocols.Pubsub/README.md).
As a libp2p protocol implementer, you may be interested in [more advanced tutorials](./docs/development/README.md). You can rewire and reconfigure the library in any way you want!

**Contributions are welcome**, kindly check the [issues](https://github.com/NethermindEth/dotnet-libp2p/issues) tab, everything there if not assigned to a person can be taken into work. More details in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Adding libp2p to your project

```sh
dotnet add package Nethermind.Libp2p
```

## Building the solution

```sh
git clone https://github.com/NethermindEth/dotnet-libp2p.git
cd ./dotnet-libp2p/libp2p/
dotnet build
dotnet test
```

The MAUI chat sample is visible in the solution but excluded from its default restore and build. Only building [that sample](./samples/maui-chat/README.md) requires the MAUI workload.

## Roadmap

The current implementation supports the protocols marked ✅ below. The roadmap tracks ongoing protocol coverage and improvements.

The target is to provide a performant well-tested implementation of a wide range of protocols that works on multiple platforms, with high throughput and low memory profile.


| Protocol | Version | Status | Package |
| -------------------- | -------------------- | ----------------- | ------- |
| TCP | tcp | ✅ | included |
| QUIC | quic-v1 | ✅ | included |
| multistream-select | /multistream/1.0.0 | ✅ | included |
| plaintext | /plaintext/2.0.0 | ✅ | included |
| noise | /noise | ✅ | included |
| yamux | /yamux/1.0.0 | ✅ | included |
| [mplex](./libp2p/Libp2p.Protocols.Mplex/README.md) | /mplex/6.7.0 | ✅ | `Nethermind.Libp2p.Protocols.Mplex` |
| tls | /tls/1.0.0 | ✅ | included |
| WebSockets | /ws, /wss | ✅ | `Nethermind.Libp2p.Protocols.WebSockets` |
| WebRTC Direct | /webrtc-direct | 🚧 | `Nethermind.Libp2p.Protocols.WebRtc` |
| Circuit Relay | /libp2p/circuit/relay/0.2.0/* | 🚧 | included |
| WebTransport |  | ⬜ help wanted |  |
| hole punching |  | ⬜ help wanted |  |
| auto-tls |  | 🚧 | `Nethermind.Libp2p.Protocols.AutoTls` |
| **Application layer** |  |  |  |
| Identify | /ipfs/id/1.0.0 | ✅ | included |
| ping | /ipfs/ping/1.0.0 | ✅ | included |
| Identify push | /ipfs/id/push/1.0.0 | ✅ | included |
| pubsub | /floodsub/1.0.0 | ✅ | included |
|  | /meshsub/1.0.0 | ✅ |  |
|  | /meshsub/1.1.0 | ✅ |  |
|  | /meshsub/1.2.0 | ✅ |  |
|  | /meshsub/1.3.0 | ✅ |  |
|  | /meshsub/2.0.0 | ⬜ help wanted |  |
| request-response |  | ✅ | included |
| perf | /perf/1.0.0 | ✅ | [sample](./samples/perf-benchmarks) |
| **Discovery** |  |  |  |
| mDns | basic w/o DNS-SD | ✅ | included |
| pubsub peer discovery | [pubsub-peer-discovery](https://github.com/libp2p/js-libp2p-pubsub-peer-discovery) | ✅ | included |
| Kademlia DHT | /ipfs/kad/1.0.0 | ✅ | `Nethermind.Libp2p.Protocols.KadDht` |

"included" protocols ship in the `Nethermind.Libp2p` package. Add the named package to use the other protocols.

⬜ - not yet implemented<br>
🚧 - work in progress<br>
✅ - basic support implemented

No plans for: quic-draft-29

## License

dotnet-libp2p is an open-source software licensed under the [MIT](https://github.com/nethermindeth/dotnet-libp2p/blob/main/LICENSE).
