# Allocation profiles

The `before` summaries profile native owned channels at `70988269b54c3da4eec47a11a6fd3c78ab46c4fc`. The `after` summaries profile the candidate based on that revision, with `AsyncSemaphore` replacing the channel's semaphore waits and linked cancellation sources. Both captures use separately frozen benchmark binaries, and their corresponding source hashes are recorded. The candidate's `-dirty` label identifies the pre-commit working tree; its exact implementation is identified by `after/source-hashes.txt`. Raw traces and profiling tools stay in the repository's ignored `.git/bookkeeping-profile` directory; existing benchmark results were not overwritten.

Each capture runs the existing throughput-mode workload under `dotnet-trace 10.0.750501` with the `gc-verbose` profile, tiered compilation disabled, and workstation GC. `small-reads` transfers 64 KiB in 64-byte fragments with no caller cancellation token. `steady-small-reads` transfers 1 MiB in 64-byte fragments with a cancellable token.

The summaries use allocation ticks after the first 1,500 ms to reduce startup and warmup effects. Each tick's `AllocationAmount64` weights the sampled object type and stack. These are sampled estimates, not exact per-type allocation totals or object counts. A few end-of-run JSON serialization allocations remain. Tracing changes timing, so these captures are diagnostic evidence and must not be used as throughput measurements. The timed workloads complete different numbers of operations, so raw weighted-byte totals also must not be interpreted as per-operation allocation changes.

| Allocation type | Small before | Small after | Cancellable steady before | Cancellable steady after |
| --- | ---: | ---: | ---: | ---: |
| Semaphore wait state machine | 41.47% | Not sampled | 27.59% | Not sampled |
| Semaphore `TaskNode` | 22.35% | Not sampled | 12.26% | Not sampled |
| `Task.WaitAsync` cancellation promise | 21.67% | Not sampled | 16.80% | Not sampled |
| Cancellation registrations | Not sampled | Not sampled | 11.34% | Not sampled |
| Cancellation callback nodes | Not sampled | 0.85% | 10.67% | Not sampled |
| Linked cancellation source | Not sampled | Not sampled | 9.20% | Not sampled |
| Task completion action | 7.00% | Not sampled | 6.01% | Not sampled |
| Buffer slice lease | 6.91% | 88.84% | 5.82% | 95.00% |

Before the change, the dominant stacks pass through `SemaphoreSlim.WaitAsyncCore`, `WaitUntilCountOrTimeoutAsync`, and `Task.WaitAsync` from channel `ReadAsync` and `WriteCoreAsync`. Passing the channel's close token makes these waits cancellable even when the caller supplies no token. With a caller token, `LinkToken` additionally allocates a linked source whose registrations cannot be reused across operations.

Those dominant task, semaphore-wrapper, and linked-source types do not appear in the candidate samples. The remaining dominant stack allocates `PooledBuffer.Slice` from `LeaseSlice` when publishing a received fragment. Its larger percentage reflects the reduction of other costs; it does not indicate an increase in per-fragment lease allocation. Public lease ownership remains unchanged.

Small-read captures contain 1,172 allocation ticks before and 118 after; cancellable steady captures contain 1,631 before and 120 after. Candidate residual shares are therefore coarse: most non-slice types have only one sampled tick. "Not sampled" does not establish zero allocation, and small differences between residual percentages are not meaningful. Separate untraced allocation measurements quantify the reduction; these profiles identify which allocation paths changed.

## Reproduce

Use the same benchmark sources and a Release build for both implementations. Keep each revision's complete benchmark output directory separate; the captures used `.git/bookkeeping-profile/baseline-bin` and `.git/bookkeeping-profile/candidate-bin`. The result files record the runtime and scenario parameters. Run profiling separately from untraced allocation and throughput comparisons.

From the repository root, configure a local tools directory:

```powershell
$profileRoot = Join-Path (Get-Location) '.git/bookkeeping-profile'
foreach ($name in @('tools', 'packages', 'http-cache', 'home', 'temp', 'TraceSummarizer')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot $name) | Out-Null
}
$env:DOTNET_CLI_HOME = Join-Path $profileRoot 'home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $profileRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $profileRoot 'http-cache'
$env:TEMP = Join-Path $profileRoot 'temp'
$env:TMP = $env:TEMP
$env:DOTNET_TieredCompilation = '0'
dotnet tool install dotnet-trace --version 10.0.750501 --tool-path (Join-Path $profileRoot 'tools')
```

Copy `summarize.cs.txt` to `.git/bookkeeping-profile/TraceSummarizer/Program.cs` and `summarize.csproj.txt` to that directory as `TraceSummarizer.csproj`. The utility references TraceEvent and its serialization dependency from the installed trace tool, so it needs no additional package. Build it in Release with warnings as errors.

Capture each scenario using the frozen benchmark binary:

```powershell
$scenario = 'steady-small-reads' # also small-reads
$variant = 'before' # use after with candidate-bin and the candidate source revision
$binaryDirectory = 'baseline-bin'
$sourceRevision = '7098826'
$trace = Join-Path $profileRoot "$variant-owned-$scenario.nettrace"
& (Join-Path $profileRoot 'tools/dotnet-trace.exe') collect --profile gc-verbose --output $trace --show-child-io -- dotnet (Join-Path $profileRoot "$binaryDirectory/Nethermind.Channels.Benchmarks.dll") owned $scenario $sourceRevision throughput 7098826
dotnet (Join-Path $profileRoot 'TraceSummarizer/bin/Release/net10.0/TraceSummarizer.dll') $trace 1500
```

The checked-in summaries retain aggregate type weights and up to two representative stacks of twelve frames per type. The summarizer emits up to four stacks of twenty frames for deeper local inspection.

Microsoft documents [`gc-verbose` allocation sampling](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) and the [TraceEvent library](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventLibrary.md). The [.NET 10 semaphore implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/Threading/SemaphoreSlim.cs) confirms the task and cancellation-wrapper allocation paths shown in these stacks.
