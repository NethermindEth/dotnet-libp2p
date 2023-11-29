**This project is no longer maintained and has been archived.**

# Multiformats.Address (cs-multiaddress)

[![](https://img.shields.io/badge/project-multiformats-blue.svg?style=flat-square)](https://github.com/multiformats/multiformats)
[![](https://img.shields.io/badge/freenode-%23ipfs-blue.svg?style=flat-square)](https://webchat.freenode.net/?channels=%23ipfs)
[![](https://img.shields.io/badge/readme%20style-standard-brightgreen.svg?style=flat-square)](https://github.com/RichardLitt/standard-readme)
[![Travis CI](https://img.shields.io/travis/multiformats/cs-multiaddress.svg?style=flat-square&branch=master)](https://travis-ci.org/multiformats/cs-multiaddress)
[![AppVeyor](https://img.shields.io/appveyor/ci/tabrath/cs-multiaddress/master.svg?style=flat-square)](https://ci.appveyor.com/project/tabrath/cs-multiaddress)
[![NuGet](https://buildstats.info/nuget/Multiformats.Address)](https://www.nuget.org/packages/Multiformats.Address/)
[![Codecov](https://img.shields.io/codecov/c/github/multiformats/cs-multiaddress/master.svg?style=flat-square)](https://codecov.io/gh/multiformats/cs-multiaddress)
[![Libraries.io](https://img.shields.io/librariesio/github/multiformats/cs-multiaddress.svg?style=flat-square)](https://libraries.io/github/multiformats/cs-multiaddress)

> [Multiaddr](https://github.com/multiformats/multiaddr) implementation in C# .NET Standard 1.6 compliant.

## Table of Contents

- [Install](#install)
- [Usage](#usage)
- [Supported protocols](#supported-protocols)
- [Maintainers](#maintainers)
- [Contribute](#contribute)
- [License](#license)

## Install

    PM> Install-Package Multiformats.Address

---

    dotnet add package Multiformats.Address

## Usage
``` cs
var ma = Multiaddress.Decode("/ip4/127.0.0.1/udp/1234");
var addresses = ma.Split();
var joined = Multiaddress.Join(addresses);
var tcp = ma.Protocols.Get<TCP>();
```

There's some extension methods included that let's you create multiaddresses of IPEndPoints, and create IPEndPoints from multiaddresses.
Some let's you create sockets directly from IP4/IP6, TCP/UDP multiaddresses.

``` cs
var socket = ma.CreateSocket();
var localEndPoint = socket.GetLocalMultiaddress();
var remoteEndPoint = socket.GetRemoteMultiaddress();
```

## Supported protocols

* DCCP
* DNS/4/6
* HTTP
* HTTPS
* IPv4
* IPv6
* IPFS (deprecated - use P2P)
* Onion
* P2P
* SCTP
* TCP
* UDP
* UDT
* Unix
* WebRTCDirect
* WebRTCStar
* WebSocket
* WebSocket Secure

## Maintainers

Captain: [@tabrath](https://github.com/tabrath).

## Contribute

Contributions welcome. Please check out [the issues](https://github.com/multiformats/cs-multiaddress/issues).

Check out our [contributing document](https://github.com/multiformats/multiformats/blob/master/contributing.md) for more information on how we work, and about contributing in general. Please be aware that all interactions related to multiformats are subject to the IPFS [Code of Conduct](https://github.com/ipfs/community/blob/master/code-of-conduct.md).

Small note: If editing the README, please conform to the [standard-readme](https://github.com/RichardLitt/standard-readme) specification.

## License

[MIT](LICENSE) © 2017 Trond Bråthen
