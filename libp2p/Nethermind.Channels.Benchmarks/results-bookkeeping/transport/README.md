# Local bulk transport comparison

Baseline: `70988269b54c3da4eec47a11a6fd3c78ab46c4fc`. The candidate replaces the
channel's semaphore and cancellation wait bookkeeping. `manifest.json` records
the exact source hashes of both revisions and their identical benchmark code.

Each fresh process establishes a loopback session, warms up five streams, then
measures 15 streams. Every stream transfers 8 MiB in the stated direction.
Opening and negotiating each stream, transferring data, validating every byte
and the total length, and completing the exchange are timed. Initial connection
establishment and warmup are excluded. Transfers use 64 KiB application chunks.

Three processes were run per revision and workload, alternating baseline and
candidate order between replicas. Other task builds, tests and benchmarks were
paused. All 36 processes completed successfully, producing 540 measured streams.
The host was Windows 10.0.26200 x64, .NET 10.0.12 / SDK 10.0.401, with 12 logical
processors, workstation GC and tiered compilation disabled.

Values are MiB/s, summarized as the median of three process medians. Brackets
show the minimum and maximum process medians.

| Stack | Direction | Before | After | Median change |
|---|---|---:|---:|---:|
| TCP / Noise / Yamux | Upload | 393.95 [377.08–414.61] | 408.52 [399.07–415.38] | +3.70% |
| TCP / Noise / Yamux | Download | 390.89 [386.20–401.32] | 413.22 [404.35–416.77] | +5.71% |
| TCP / TLS / Yamux | Upload | 12.33 [12.33–12.49] | 12.88 [12.82–12.93] | +4.47% |
| TCP / TLS / Yamux | Download | 12.39 [12.33–12.56] | 12.69 [12.68–12.74] | +2.42% |
| QUIC v1 | Upload | 23.06 [22.90–26.11] | 35.11 [33.83–37.60] | +52.24% |
| QUIC v1 | Download | 24.72 [22.88–26.54] | 33.13 [33.06–34.02] | +34.01% |

All six median rates increased in this run. Noise upload ranges overlap; the
other five workloads have separated observed process ranges. QUIC shows the
largest local improvement. Three processes on one host do not establish a
universal speedup or predict remote-network performance. These 8 MiB transfers
also differ from the earlier 32 KiB short-exchange benchmark, so their absolute
rates should not be compared as if the workloads were identical.

See `summary.json` for unrounded process medians and changes, and each process
JSON for its individual stream samples and environment. `run-comparison.ps1.txt`
preserves the exact measured runner and matches `runnerSha256` in the manifest.
After this run, the active runner's baseline check was extended to root build
inputs; the recorded baseline was independently verified clean for those inputs.
This guard-only change does not alter the timed workload.

To repeat the comparison, follow the
[transport benchmark instructions](../../../Libp2p.Transport.Benchmarks/README.md)
and select a new output directory. The current runner checks both tracked and
untracked baseline changes and refuses to overwrite existing results.
