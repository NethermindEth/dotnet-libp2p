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

The connection accepts payloads up to the protocol's 1 MiB limit and sends data in frames of at most 16 KiB (smaller when needed to fit the secure transport's preferred write size). Local resource limits are 512 inbound and 512 outbound stream reservations per connection (1,024 combined), 64 queued incoming frames per stream, 4 MiB of unread bytes per stream, 16 MiB across the connection, and 64 queued ordinary outgoing frames. Unread byte limits include a delivery blocked on the application. Streams exceeding an incoming buffer limit are reset without blocking delivery to other streams. New remote streams beyond the inbound reservation limit are reset; local requests beyond the outbound limit fail. Streams in one direction do not consume admission capacity in the other.

Resets use a separate FIFO and ordinary-queue saturation never tears down the connection. Each accepted stream retains its reservation until local cleanup and any required reset transmission both finish, with at most one pending reset per reservation, still charged to its original direction. Retired entries awaiting transmission discard late frames. Rejected remote opens have a separate budget of 64 queued or in-flight resets, bounding all accepted and rejected reset obligations to 1,088 per connection. If that budget is exhausted, the reader pauses until a reset finishes; this bounds memory but can delay unrelated inbound traffic while the peer is not reading. The first such pause logs a warning per connection.

The single transport writer finishes the current frame, then gives resets priority in bursts of at most eight before serving an eligible ordinary frame. Each queue preserves FIFO order, and cancelled ordinary frames are skipped. Opening frames, data, and graceful closes retain their existing order. Transport progress is required to drain either queue.

Malformed or non-minimal varint encodings, reserved flags, duplicate live stream IDs, oversized frames, and nonempty close/reset payloads close the connection. Messages after a stream's receive half closes reset that stream. There is no wire-level flow-control extension or new handshake.

Wire behavior follows the [Mplex specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/mplex/README.md) and [unsigned varint specification](https://github.com/multiformats/unsigned-varint/blob/dd184f1ce89c81808503bb719b5fcb13844d08f3/README.md). Mplex is deprecated for general libp2p use, but the [Ethereum consensus networking specification](https://github.com/ethereum/consensus-specs/blob/master/specs/phase0/p2p-interface.md#multiplexing) requires it for TCP compatibility and prefers Yamux when available.
