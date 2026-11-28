# Local transport throughput

This console measures loopback TCP/Noise/Yamux, TCP/TLS/Yamux and QUIC streams.
The original invocation remains valid:

```powershell
dotnet libp2p/Libp2p.Transport.Benchmarks/bin/Release/net10.0/Libp2p.Transport.Benchmarks.dll result.json tcp-noise-yamux upload
```

Optional fourth and fifth arguments select the number of measured streams and
the payload bytes per stream. Defaults are one measured stream and 32 KiB.
Sample count and payload must be positive integers. For example, 15 streams of
8 MiB each:

```powershell
dotnet libp2p/Libp2p.Transport.Benchmarks/bin/Release/net10.0/Libp2p.Transport.Benchmarks.dll result.json tcp-noise-yamux upload 15 8388608
```

Stacks are `tcp-noise-yamux`, `tcp-tls-yamux`, and `quic-v1`; bulk directions are
`upload` and `download`. The `stream-open-exchange` metric keeps its one-byte
exchange and reports milliseconds; it rejects a payload argument.

Each process establishes one session, warms up one stream for the default
single-sample invocation or five streams for multiple samples, then measures
each stream separately. Connection establishment and warmup are excluded.
Stream opening, negotiation, transfer and completion are included. Payload is
sent in 64 KiB chunks, and both endpoints validate the received byte count.
They also check every payload segment against the expected byte value using
`IndexOfAnyExcept`; this validation cost is included in the timed transfer.
The process has a two-minute timeout. This is local loopback performance; remote
latency, packet loss and network hardware are outside the measurement.
