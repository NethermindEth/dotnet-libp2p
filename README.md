<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://github.com/libp2p/libp2p/blob/master/logo/white-bg-2.png?raw=true">
    <source media="(prefers-color-scheme: light)" srcset="https://github.com/libp2p/libp2p/blob/master/logo/black-bg-2.png?raw=true">
    <img alt="libp2p" src="https://github.com/libp2p/libp2p/blob/master/logo/black-bg-2.png?raw=true" height="96">
  </picture>
</p>

# dotnet-libp2p

The project aims to implement [libp2p](https://libp2p.io) to unlock building .NET peer-to-peer applications using a battle-tested specification of network communication of the new age.

As an application developer, you may consider reading [quick start](./docs/quick-start.md).
As a stack implementer, you may be interested in [more advanced tutorials](./docs/development/README.md).
**Contributors are welcomed**, kindly check the issues tab, everything there if not assigned to a person can be taken into work. More details in [CONTRIBUTING.md](./CONTRIBUTING.md).

## Roadmap

From the beginning, the target is to provide a performant well-tested implementation that works on multiple platforms. With high throughput and low memory profile. The modules set to be implemented firstly should cover basic P2P application needs and include:

- [Identify](https://github.com/libp2p/specs/blob/master/identify/README.md)
- [Plaintext](https://github.com/libp2p/specs/blob/master/plaintext/README.md)
- [Discovery](https://github.com/libp2p/specs/blob/master/discovery/mdns.md)
- [Pubsub](https://github.com/libp2p/specs/tree/master/pubsub)
- [Mplex](https://github.com/libp2p/specs/blob/master/mplex/README.md)

## License

dotnet-libp2p is an open-source software licensed under the [MIT](https://github.com/nethermindeth/dotnet-libp2p/blob/main/LICENSE).
