# Multistream protocol

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/connections#multistream-select)

Incoming negotiation messages are limited to 1,024 payload bytes, including the
trailing newline and excluding the varint length prefix. This payload-length limit matches
[go-multistream v0.6.1](https://github.com/multiformats/go-multistream/blob/v0.6.1/multistream.go#L262-L290).
The comparison concerns the length limit only. This implementation also accepts
non-minimal integer varint encodings within its existing bounds, unlike
[go-varint](https://github.com/multiformats/go-varint/blob/v0.0.7/varint.go#L68-L98).
Oversized messages and invalid integer length prefixes abort negotiation before
reading the payload.
