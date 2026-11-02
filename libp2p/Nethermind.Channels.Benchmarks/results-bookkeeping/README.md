# Reusable channel wait bookkeeping

This comparison measures revision `70988269b54c3da4eec47a11a6fd3c78ab46c4fc` against the reusable-waiter change. Both already include pooled payloads and async method state. The candidate replaces four task-based semaphore gates per channel direction with an internal binary semaphore that caches at most two consumed waiters per gate. Pending waits register caller and shutdown cancellation directly, avoiding linked cancellation sources. Both registrations finish disposal before the completion source is reset and reused. Public buffer and slice lease identities keep their existing lifetime and disposal semantics.

The [allocation profiles](profiles/README.md) identify the removed wait/task/cancellation machinery. The [transport comparison](transport/README.md) measures actual loopback streams separately from the synthetic channel cases below.

## Allocations

Median allocated bytes per complete payload operation, using process-wide counters, warmed pools, three warmup batches and five measured batches. Payload bytes and retained-read lifetime are validated; all producer/consumer tasks finish before the counter interval ends. These totals measure allocations, not memory retained by pools.

| Workload | Native before → after | Core owned before → after | Core legacy before → after |
|---|---:|---:|---:|
| Full 64 KiB | 3,289 → 2,843 | 3,643 → 3,198 | 69,290 → 68,813 |
| 4096 one-byte fragments | 3,264,472 → 176,216 | 3,258,864 → 173,680 | 3,254,656 → 177,888 |
| 1024 completed 64-byte reads | 807,732 → 44,210 | 787,128 → 44,912 | 893,060 → 135,122 |
| Forward 64 KiB | 6,147 → 5,219 | 6,788 → 5,948 | 72,270 → 71,351 |
| Sustained 1 MiB, cancellable | 20,068,504 → 659,480 | 19,959,160 → 664,512 | 21,313,456 → 2,105,528 |

Core owned allocations fall 94.7% for fragmented pending reads, 94.3% for completed small reads, and 96.7% for sustained cancellable reads. Full-block and forwarding allocations fall about 12%. Legacy reads still allocate detached payload arrays to preserve indefinitely retainable sequences.

The remaining small-read cost is primarily one independently disposable slice lease per owned publication/read. These public identities are not recycled: disposal remains idempotent even when callers retain old references. Wait caches are bounded, while simultaneously outstanding caller operations and retained payload leases still have their own lifetimes.

## Channel throughput

Completed payload operations per second. Each case uses three fresh processes per revision, at least one second of warmup and five samples of at least 500 ms. The table shows the median of process medians and their minimum–maximum range. Natural collections remain included; no forced GC runs during throughput samples.

| Variant | Workload | Before | After | Median change |
|---|---|---:|---:|---:|
| Native | 4096 one-byte fragments | 21.816 [21.167–22.850] | 48.342 [45.475–48.868] | +121.6% |
| Native | 1024 completed 64-byte reads | 66.486 [65.534–87.771] | 143.780 [142.703–146.003] | +116.3% |
| Native | Sustained 1 MiB, cancellable | 3.537 [3.385–3.792] | 8.275 [7.987–9.572] | +134.0% |
| Core owned | 4096 one-byte fragments | 20.630 [20.437–21.472] | 51.529 [49.575–55.578] | +149.8% |
| Core owned | 1024 completed 64-byte reads | 53.048 [50.545–55.767] | 101.568 [98.970–106.828] | +91.5% |
| Core owned | Sustained 1 MiB, cancellable | 2.784 [2.656–3.220] | 5.761 [5.701–5.985] | +106.9% |

All six observed process ranges are separated. Core owned throughput is 1.91×–2.50× the baseline in these workloads. The sustained case transfers 1 MiB per operation, so its operation rate also equals MiB/s. These measurements exercise very small channel handoffs and include payload checking and channel teardown; they are not socket bandwidth measurements.

## Local transport speed

The separate [8 MiB loopback comparison](transport/README.md) uses three fresh processes per stack/direction/revision and 15 measured streams after five warmups. Every byte and the total length are checked. It includes stream negotiation, transfer and completion, excluding initial connection establishment.

| Stack | Upload before → after | Download before → after |
|---|---:|---:|
| TCP / Noise / Yamux | 393.95 → 408.52 MiB/s (+3.7%) | 390.89 → 413.22 MiB/s (+5.7%) |
| TCP / TLS / Yamux | 12.33 → 12.88 MiB/s (+4.5%) | 12.39 → 12.69 MiB/s (+2.4%) |
| QUIC v1 | 23.06 → 35.11 MiB/s (+52.2%) | 24.72 → 33.13 MiB/s (+34.0%) |

Noise upload process ranges overlap; the other five transport workloads have separated observed ranges. These are local results on one host, not a guarantee for remote networks or other hardware.

## Reproduce

Measured on Windows 10.0.26200 x64, .NET 10.0.12 / SDK 10.0.401, 12 logical processors, workstation GC, with tiered compilation disabled. Other task builds, tests and benchmarks were paused. The same benchmark source is used for both revisions; production and benchmark hashes are saved with every result group.

From the repository root, create a detached baseline worktree if it does not already exist. Keep its production sources unchanged. Choose new output directories to preserve these recorded results:

```powershell
git worktree add --detach .worktrees/allocations-bookkeeping-before 70988269b54c3da4eec47a11a6fd3c78ab46c4fc
$runner = './libp2p/Nethermind.Channels.Benchmarks/run.ps1'
$baseline = '.worktrees/allocations-bookkeeping-before'
$output = './libp2p/Nethermind.Channels.Benchmarks/results-bookkeeping-repeat'
& $runner -SourceRoot $baseline -OutputDirectory "$output/before"
& $runner -OutputDirectory "$output/after"
foreach ($replica in 1..3) {
    $order = if ($replica % 2 -eq 1) { @('before', 'after') } else { @('after', 'before') }
    foreach ($side in $order) {
        $source = if ($side -eq 'before') { $baseline } else { '.' }
        & $runner -SourceRoot $source -OutputDirectory "$output/$side-throughput-$replica" -Variants owned,core-owned -Scenarios fragmented-waitall,small-reads,steady-small-reads -Measurement throughput -Replicas 1
    }
}
```

Allocation cases run baseline then candidate. Throughput revision order alternates for each process replica. The `before-throughput-*` and `after-throughput-*` directories contain the 36 throughput process records; `before/` and `after/` contain the 30 allocation case records. `summary.json` aggregates their unrounded medians. Profiling runs are separate and are never used for timing claims. Earlier measurement directories remain unchanged.
