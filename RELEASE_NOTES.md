<!-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited -->
<!-- SPDX-License-Identifier: MIT -->

# Release notes

## Unreleased

### Kademlia custom builder compatibility (PR #420)

Kademlia registration now requires inbound stream limits, including with default
options. A custom `IPeerFactoryBuilder` that implements only the two-argument
`AddProtocol(instance, isExposed)` throws `NotSupportedException` when used with
`AddKadDht`, `WithKadDht`, or `AddKadDhtProtocols`. This requirement cannot be
disabled through configuration: zero stream limits are invalid.

To migrate, implement the three-argument `AddProtocol` overload and enforce its
`InboundStreamLimit`, or support `IProtocolFactoryBuilder` and honor
`ProtocolRef.InboundLimit`. See the [Kademlia configuration and compatibility
notes](libp2p/Libp2p.Protocols.KadDht/README.md) for details.
