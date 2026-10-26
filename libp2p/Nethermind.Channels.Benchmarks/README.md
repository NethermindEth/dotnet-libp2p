# Channel allocation comparison

This console compares receive-buffer and channel allocations with the
same deterministic input and reader workload. It uses
`GC.GetTotalAllocatedBytes(precise: true)` so allocations on asynchronous
continuation threads are counted. It does not use a thread-local allocation
counter or leave producer tasks running after a measurement.

The latest [reusable-waiter comparison](https://github.com/NethermindEth/dotnet-libp2p/blob/tcp-receive-buffer-ownership/libp2p/Nethermind.Channels.Benchmarks/results-bookkeeping/README.md)
measures this shared engine with the dependent transport integration. Native
tiny-read allocations fall about 95–97%; local channel and transport throughput
results include process ranges. The original integration snapshot below is preserved.

## Reproduce

From the repository root, using PowerShell and the .NET 10 SDK:

```powershell
./libp2p/Nethermind.Channels.Benchmarks/run.ps1
```

The default builds and measures only the native `owned` variant. The benchmark
is included in `libp2p/Libp2p.slnx` and needs no historical worktree or Core receive
pump to build. Fresh measurements go to `results-current/`; the checked-in
`results/` files retain the original measurements and their provenance paths.

Request historical variants explicitly:

```powershell
./libp2p/Nethermind.Channels.Benchmarks/run.ps1 -BaselineOnly
```

This creates a detached worktree at `.worktrees/allocations-pr428`, verifies its
pinned revision and unchanged Core sources, and builds the historical comparison
with `BaselineOnly=true`. Only that build defines `HISTORICAL` and references the
old Core implementation. The worktree is excluded through Git's local exclude
file and is not removed afterward. `-Variants` can select or combine named
variants; the runner builds current and historical variants separately.
`-BaselineRoot` can reuse an existing pinned worktree. `-SourceRoot` selects a
different native source checkout and supports both the current `libp2p` layout
and the earlier `src/channels` layout.

Each variant/scenario runs in its own quiet process, with tiered compilation
disabled, three warmup batches and five measured batches. The program checks the
complete byte payload without allocating comparison arrays. It awaits every
producer, reader and forwarding task, then closes both channel endpoints before
ending the operation. Channel creation, buffer allocation, asynchronous state,
verification and teardown are all included. Input payload creation, JSON output
and process startup are outside the measured interval. Forced collections happen
between samples, outside the counter interval. The pool is warmed before measuring.

The process emits JSON only after measurement. Raw counters, the median/minimum/
maximum bytes per operation, environment details and SDK information are retained
under the selected output directory, alongside source hashes for the native implementation.
Timing is recorded for diagnosing anomalous runs; these runs
are an allocation comparison, not a throughput benchmark.

## Variants and scenarios

The `main-listener` and `main-dialer` variants reproduce the TCP receive allocation
shapes from `cf72a0c9e9420ff7b74d28e8b9feef1a91a8cc05`: a new 64 KiB buffer per
receive, with the dialer additionally copying the received prefix. The `managed`
variant executes the actual `IWriter.WriteFromAsync` from the original PR #428,
revision `70b6c7321a2471dca985c6d15e8de30d0721c56b`. Its `Channel.cs` is identical to
the main revision, allowing all three variants to use the same pinned baseline
assembly.

The `owned` variant uses the current `Nethermind.Channels` receive pump and
disposes every read result. Its forwarding case retains a slice and forwards
that lease without copying the payload.

The source fills the supplied `Memory<byte>` synchronously, like an already
available socket read, with distinct bytes from a precomputed payload. Socket,
QUIC, TLS and operating-system allocations are excluded so the comparison
isolates the buffer and channel API. The final read that reports EOF is included:
a completely filled managed block needs another block for that final read.

| Scenario | Payload | Receive fragments | Application reads | Operations per sample |
|---|---:|---:|---:|---:|
| `full-block` | 64 KiB | 64 KiB | One exact read | 128 |
| `fragmented-waitall` | 4 KiB | 4096 × 1 byte | One pending exact read | 1 |
| `small-reads` | 64 KiB | 1024 × 64 bytes | 1024 completed reads | 4 |
| `forwarding` | 64 KiB | 64 KiB | One exact read forwarded through a second channel | 128 |

Managed allocation totals are not retained-memory measurements. They include
channel and task overhead, while pooled arrays can remain cached after leases
are released. Memory ownership and pool return behavior also need lifecycle tests.

## Recorded results

These are the initial integration measurements at `90139c8`, before pooling
asynchronous channel state. The later [paired allocation and throughput comparison](https://github.com/NethermindEth/dotnet-libp2p/blob/tcp-receive-buffer-ownership/libp2p/Nethermind.Channels.Benchmarks/README.md#async-state-pooling-comparison)
measures the optimization with the transport integration in #428. Against its
prior pooled implementation, native small-read allocations fell 37.5% and
fragmented-read allocations fell 19.5%. Core owned small-read allocations fell
48.0%. Synthetic and loopback transport throughput ranges overlapped.

Measured on Windows 10.0.26200, x64, .NET 10.0.12 / SDK 10.0.401, workstation GC,
12 logical processors. Values below are median allocated bytes per operation,
rounded to whole bytes. Each operation transfers the complete payload listed
above, not just one fragment.

| Scenario | Main listener | Main dialer | Original #428 managed | Owned channels |
|---|---:|---:|---:|---:|
| Full 64 KiB block | 134,427 | 199,987 | 134,515 | 3,668 |
| 4096 one-byte fragments, pending exact read | 272,457,312 | 272,593,792 | 3,823,496 | 4,040,400 |
| 1024 completed 64-byte reads | 68,364,316 | 68,456,096 | 1,247,024 | 1,294,212 |
| Forward 64 KiB through two channels | 137,263 | 202,818 | 137,356 | 6,826 |

Compared with the managed receive arena, owned channels reduce allocation by
97.3% for full blocks and 95.0% for forwarding in this run. Tiny fragmented reads
allocate 5.7% more, and completed small reads about 3.8% more: explicit leases and
asynchronous channel bookkeeping still allocate even when payload storage is
pooled. Pooling is therefore not an across-the-board allocation improvement.
Both designs avoid main's receive-capacity allocation for every tiny fragment.

The raw JSON files include sample ranges. The owned small-read sample range is
1,287,678–1,296,594 bytes per operation; the managed range is
1,239,168–1,260,764. Pool caches and asynchronous scheduling affect individual
samples, so the table should not be read as an exact universal cost model.
