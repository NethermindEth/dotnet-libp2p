# Transport check for pooled async state machines

The baseline is `f10bf73`, before pooling channel read/write state machines. The candidate adds pooled builders to the shared channel and Core adapters, with task conversion for synchronous stream operations. Both revisions use the same enhanced transport harness. `manifest.json` records revisions and SHA-256 hashes of the implementation and harness files.

Each fresh process establishes one loopback session, performs five warmup streams, and measures 100 further streams. Every stream transfers 32 KiB in the stated direction. The timed operation includes opening and negotiating that stream, transferring its bytes, and finishing the exchange. Connection establishment and warmup are excluded. The benchmark validates the transferred byte counts. This measures short stream exchanges, not sustained bulk bandwidth.

Three processes were run per revision and workload. Baseline/candidate order alternated between replicas. No other task builds, tests, or benchmarks ran during measurement. The runtime was .NET 10.0.12 on Windows x64 with workstation GC and tiered compilation disabled; each result records the environment and timestamp.

| Stack | Direction | Before MiB/s | After MiB/s | Change |
| --- | --- | ---: | ---: | ---: |
| TCP / Noise / Yamux | Upload | 32.002 | 32.505 | +1.57% |
| TCP / Noise / Yamux | Download | 29.960 | 30.065 | +0.35% |
| TCP / TLS / Yamux | Upload | 8.773 | 8.721 | -0.59% |
| TCP / TLS / Yamux | Download | 8.617 | 8.716 | +1.15% |
| QUIC v1 | Upload | 11.925 | 11.917 | -0.06% |
| QUIC v1 | Download | 12.944 | 13.311 | +2.84% |

Values are the median of the three process medians. Replica ranges overlap for every workload; these measurements show no material transport regression and do not establish a throughput improvement. See `summary.json` for all process medians and ranges, and the per-process JSON files for individual samples.

The harness accepts an optional fourth argument for the sample count:

```powershell
$env:DOTNET_TieredCompilation = '0'
$env:DOTNET_gcServer = '0'
dotnet libp2p/Libp2p.Transport.Benchmarks/bin/Release/net10.0/Libp2p.Transport.Benchmarks.dll result.json tcp-noise-yamux upload 100
```

Stacks are `tcp-noise-yamux`, `tcp-tls-yamux`, and `quic-v1`; directions are `upload` and `download`. Run the same command against both Release builds, changing the output path and alternating revision order. The original three-argument invocation still performs one warmup and one measured stream; `smoke-default.json` verifies that behavior.
