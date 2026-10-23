# Multistream protocol

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/connections#multistream-select)

Incoming negotiation messages are limited to 1,024 payload bytes, including the
trailing newline and excluding the varint length prefix. This matches
[go-multistream v0.6.1](https://github.com/multiformats/go-multistream/blob/v0.6.1/multistream.go#L262-L290).
Oversized messages and invalid integer length prefixes abort negotiation before
reading the payload.
