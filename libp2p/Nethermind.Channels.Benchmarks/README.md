# Channel allocation and throughput comparison

This console compares receive-buffer and channel allocations with the
same deterministic input and reader workload. It uses
`GC.GetTotalAllocatedBytes(precise: true)` so allocations on asynchronous
continuation threads are counted. It does not use a thread-local allocation
counter or leave producer tasks running after a measurement.

## Reproduce

From the repository root, using PowerShell and the .NET 10 SDK:

```powershell
./libp2p/Nethermind.Channels.Benchmarks/run.ps1
```

The default run measures current native channels, Core owned reads and Core
legacy reads into `results-current/`. To rerun the pinned historical variants,
use `run.ps1 -BaselineOnly`. The original `results/` records remain the snapshot
published with the channel integration in commit `90139c8` (PR #429).

The project is included in `libp2p/Libp2p.slnx`. Its default build and current
measurements use the local Core and channel projects and require no historical
worktree. Only explicit historical variants (`main-listener`, `main-dialer`, or
`managed`, including `-BaselineOnly`) create the detached
`.worktrees/allocations-pr428` worktree and verify its revision. That worktree is
excluded through `.git/info/exclude` and is not removed afterward.

Each allocation variant/scenario runs in its own quiet process, with tiered compilation
disabled, three warmup batches and five measured batches. The program checks the
complete byte payload without allocating comparison arrays. It awaits every
producer, reader and forwarding task, then closes both channel endpoints before
ending the operation. Channel creation, buffer allocation, asynchronous state,
verification and teardown are all included. Input payload creation, JSON output
and process startup are outside the measured interval. Forced collections happen
between samples, outside the counter interval. The pool is warmed before measuring.

The process emits JSON only after measurement. Raw counters, the median/minimum/
maximum bytes per operation, environment details and SDK information are retained
under the selected output directory, alongside source hashes for current code.
Timing in allocation mode is recorded for diagnosing anomalous runs; its short
samples are not used for throughput claims. The separate throughput mode below
uses longer measurements and independent process replicas.

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

The `core-owned` variant calls Core's receive facade, reads with `ReadOwnedAsync`,
disposes each result, and forwards retained slices. The `core-legacy` variant uses
the same receive facade with ordinary `ReadAsync` and forwards its stable
sequences. It also retains every returned sequence through later reads and
channel teardown, checking the bytes again afterward. Its observation array is
allocated before measurement and reused, so lifetime checks add no per-operation
allocation to the result. Current variants are compiled and run separately
from historical cases: current and historical Core have the same assembly
identity, and must not be mixed in one process. A compile guard rejects historical
variant names in the current-Core executable.

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
| `steady-small-reads` | 1 MiB | 16,384 × 64 bytes | 16,384 reads through one channel, with a cancellable token | 1 |

Managed allocation totals are not retained-memory measurements. They include
channel and task overhead, while pooled arrays can remain cached after leases
are released. Memory ownership and pool return behavior also need lifecycle tests.

## Channel integration snapshot (#429)

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

## Core receive integration snapshot (f10bf73)

The receive facade at `f10bf73` uses the shared pooled pump. Its results, separate
from the initial integration snapshot above, are saved under `results-current/`
with that revision's Core and native source hashes. The optimization comparison
below supersedes these numbers for the current implementation.

| Scenario | Original #428 managed | Native at f10bf73 | Core owned reads | Core legacy reads |
|---|---:|---:|---:|---:|
| Full 64 KiB block | 134,515 | 3,739 | 4,343 | 69,907 |
| 4096 one-byte fragments, pending exact read | 3,823,496 | 4,044,472 | 4,042,432 | 4,049,624 |
| 1024 completed 64-byte reads | 1,247,024 | 1,294,616 | 1,542,494 | 1,627,560 |
| Forward 64 KiB through two channels | 137,356 | 6,896 | 8,110 | 73,605 |

Core owned reads reduce bulk allocation by 96.8% and forwarding allocation by
94.1% relative to the original managed pump. Legacy reads detach storage so
returned sequences stay valid indefinitely; their reductions are 48.0% and
46.4% respectively.

There is a material cost for small operations. Core owned fragmented reads
allocate 5.7% more than the managed pump, and completed small reads 23.7% more;
the legacy-read increases are 5.9% and 30.5%. The receive facade forwards directly
to the shared writer and records successful-write metrics synchronously, so its
fragmented-read allocation is close to native. Small reads still pay Core's
asynchronous read-result adaptation cost, and legacy reads also allocate detached
arrays. The measurements support bulk savings, not a claim that every allocation
pattern improves.

## Async state pooling comparison

The `results-optimization/before/` and `after/` directories compare the pooled
receive implementation at `f10bf73bfbbbb06f41d6cecee5f0432becde87d9` with the
async state pooling change. Both use the same benchmark source, whose SHA256
hashes are stored beside the production source hashes. Earlier `results/` and
`results-current/` snapshots are preserved. These measurements use the same
machine and runtime as the tables above.

To select another production source tree while retaining the identical benchmark
code, pass `-SourceRoot`. The project also recognizes the former `src/channels`
layout in pinned source trees. Raw historical result paths and hashes retain
their original provenance after the project move. For example:

```powershell
git worktree add --detach .worktrees/allocations-pooled-before f10bf73bfbbbb06f41d6cecee5f0432becde87d9
$benchmark = './libp2p/Nethermind.Channels.Benchmarks/run.ps1'
& $benchmark -SourceRoot .worktrees/allocations-pooled-before -OutputDirectory libp2p/Nethermind.Channels.Benchmarks/results-optimization/before
& $benchmark -OutputDirectory libp2p/Nethermind.Channels.Benchmarks/results-optimization/after
& $benchmark -SourceRoot .worktrees/allocations-pooled-before -OutputDirectory libp2p/Nethermind.Channels.Benchmarks/results-optimization/before -Variants owned,core-owned -Scenarios fragmented-waitall,small-reads,steady-small-reads -Measurement throughput
& $benchmark -OutputDirectory libp2p/Nethermind.Channels.Benchmarks/results-optimization/after -Variants owned,core-owned -Scenarios fragmented-waitall,small-reads,steady-small-reads -Measurement throughput
```

Throughput mode runs three independent processes per variant/scenario. Each
warms up for at least one second, then takes five samples of at least 500 ms,
finishing the last complete operation. It records exact operation counts and
elapsed times, includes natural collections, and does not force GC between
samples. Tiered compilation remains disabled. Baseline and candidate run
sequentially with other builds and tests paused. This measures the synthetic
channel workload, including verification and teardown; it does not measure
socket, TLS or QUIC throughput. Rates are summarized using the median of the
three per-process medians, with the range of those medians shown to expose
process variability.

The sustained case transfers 1 MiB through one channel and receive pump, using
16,384 receive/read pairs. Unlike the shorter cases, both the pump and reader
receive a cancellable token; the token is never cancelled. Channel creation and
teardown occur once per MiB, so their fixed costs are amortized. Legacy results
remain retained through teardown and are checked again afterward, as in the
other legacy cases.

Median allocated bytes per complete operation:

| Scenario | Native before → after | Core owned before → after | Core legacy before → after |
|---|---:|---:|---:|
| Full 64 KiB block | 3,753 → 3,288 | 4,361 → 3,643 | 69,920 → 69,272 |
| 4096 one-byte fragments | 4,043,936 → 3,255,560 | 4,043,344 → 3,253,664 | 4,048,672 → 3,325,552 |
| 1024 completed 64-byte reads | 1,290,010 → 805,946 | 1,540,410 → 801,746 | 1,623,578 → 887,428 |
| Forward 64 KiB | 6,910 → 6,107 | 8,131 → 6,780 | 73,620 → 72,218 |
| Sustained 1 MiB, cancellable | 27,830,720 → 19,974,088 | 31,629,248 → 19,909,728 | 33,040,024 → 21,327,936 |

Core owned allocations fall 19.5% for fragmented pending reads, 48.0% for
completed small reads, and 37.1% for sustained cancellable reads. Native
sustained allocations fall 28.2%; legacy Core allocations fall 35.4%. The legacy
path still allocates detached payload arrays to preserve indefinite lifetime.

Completed payload operations per second, median [minimum–maximum] of the three
process medians:

| Variant | Scenario | Before | After | Median change |
|---|---|---:|---:|---:|
| Native | 4096 one-byte fragments | 22.17 [20.97–24.55] | 23.41 [20.84–24.30] | +5.6% |
| Native | 1024 completed 64-byte reads | 64.92 [62.52–65.23] | 67.46 [60.82–68.13] | +3.9% |
| Native | Sustained 1 MiB, cancellable | 3.247 [3.207–3.248] | 3.231 [3.153–3.487] | -0.5% |
| Core owned | 4096 one-byte fragments | 21.87 [21.84–22.17] | 22.45 [21.88–22.75] | +2.6% |
| Core owned | 1024 completed 64-byte reads | 50.52 [44.52–51.00] | 57.27 [49.67–58.72] | +13.4% |
| Core owned | Sustained 1 MiB, cancellable | 2.586 [2.485–2.602] | 2.925 [2.566–2.931] | +13.1% |

Process ranges overlap in every workload. The allocation reduction is clear,
but these throughput results do not establish a universal speedup or regression.
The [loopback transport comparison](results-optimization/transport/README.md)
also found overlapping ranges for TCP/Noise, TCP/TLS, and QUIC across upload and
download. It measures 100 short stream exchanges per process, including stream
negotiation, rather than sustained bulk bandwidth. The cancellable workload still allocates substantially:
pooling state machines does not eliminate channel handoff, cancellation-wait or
lease bookkeeping. Against the original managed #428 snapshot, current Core
owned allocation is now about 14.9% lower for fragmented pending reads and 35.7%
lower for completed small reads, replacing the earlier increases in those cases.
