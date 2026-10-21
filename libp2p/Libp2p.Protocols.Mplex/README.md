# Mplex

`Nethermind.Libp2p.Protocols.Mplex` implements `/mplex/6.7.0` for compatibility with peers that require the legacy TCP multiplexer. The standard stack continues to use Yamux. Mplex has no stream flow control; prefer Yamux when both peers support it, or QUIC's native streams.

Reference this package alongside `Nethermind.Libp2p`, then override the standard builder's multiplexer list:

```csharp
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

public class CompatibilityBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
{
    protected override ProtocolRef[] Muxers() => [Get<YamuxProtocol>(), Get<MplexProtocol>()];
}
```

Register `CompatibilityBuilder` as your `IPeerFactoryBuilder`. The order prefers Yamux while accepting Mplex-only peers. To require Mplex, return only `Get<MplexProtocol>()`. It applies to stream transports; QUIC still uses its own streams.

Each endpoint can independently initiate stream ID zero. Stream close ends only the sender's write direction; queued bytes arrive before EOF and the receiver can still reply. Reset aborts both directions. Unknown frames for retired streams are consumed without recreating them, and names are ignored.

The connection accepts payloads up to the protocol's 1 MiB limit and sends data in frames of at most 16 KiB (smaller when needed to fit the secure transport's preferred write size). Local resource limits are 512 live streams, 64 queued incoming frames per stream, 4 MiB of unread bytes per stream, 16 MiB across the connection, and 64 queued outgoing frames. Unread byte limits include a delivery blocked on the application. Streams exceeding an incoming buffer limit are reset without blocking delivery to other streams. New remote streams beyond the live-stream limit are reset; local requests fail. A saturated control queue closes the connection.

Malformed or non-minimal varint encodings, reserved flags, duplicate live stream IDs, oversized frames, and nonempty close/reset payloads close the connection. Messages after a stream's receive half closes reset that stream. There is no wire-level flow-control extension or new handshake.

Wire behavior follows the [Mplex specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/mplex/README.md) and [unsigned varint specification](https://github.com/multiformats/unsigned-varint/blob/dd184f1ce89c81808503bb719b5fcb13844d08f3/README.md). Mplex is deprecated for general libp2p use, but the [Ethereum consensus networking specification](https://github.com/ethereum/consensus-specs/blob/master/specs/phase0/p2p-interface.md#multiplexing) requires it for TCP compatibility and prefers Yamux when available.
