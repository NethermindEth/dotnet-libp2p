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

## Before and after bookkeeping changes

Run from the repository root after stopping other builds, tests and benchmarks:

```powershell
./libp2p/Libp2p.Transport.Benchmarks/run-comparison.ps1
```

The runner creates `.worktrees/transport-bookkeeping-before` at
`70988269b54c3da4eec47a11a6fd3c78ab46c4fc`, checks that its production sources are
unchanged, and copies the same benchmark source into it. It builds both that
revision and the current workspace in Release with warnings as errors. The
default comparison uses 8 MiB per stream, 15 measured streams after five warmups,
and three fresh processes per stack/direction/revision. Baseline/candidate order
alternates between replicas. Tiered compilation and server GC are disabled for
both revisions.

Use `-Samples`, `-PayloadBytes`, and `-Replicas` to change those counts. Use
`-OutputDirectory` for a different destination; the runner refuses to overwrite
an existing results directory. It retains raw samples, process medians, source
hashes for the benchmark's project dependencies, revisions and SDK information.
The default destination is
`libp2p/Nethermind.Channels.Benchmarks/results-bookkeeping/transport/`.
`summary.json` reports the median of
process medians and all replica medians. Compare those ranges alongside the
percentage change; small changes within process variation do not establish a
speedup or regression.

Large payloads amortize stream setup over more data. The original 32 KiB results
remain useful for short exchanges, but should not be presented as sustained
bulk bandwidth.

The [recorded bookkeeping comparison](../Nethermind.Channels.Benchmarks/results-bookkeeping/transport/README.md)
contains the 8 MiB results, raw samples, process ranges and source provenance.
