# Identify protocol

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/identify)

Stored protocol metadata keeps every matching locally installed protocol, using
the local stack's order and strings, then fills any remaining slots up to 64 with
other advertised IDs in their received order. IDs are distinct; unknown IDs are
at most 128 UTF-8 bytes. Locally configured IDs reuse their local string even when
longer. The entry count is bounded by the greater of 64 and the number of
distinct locally installed protocol IDs. Remote ordering therefore cannot hide
a supported protocol version behind unrelated IDs.
