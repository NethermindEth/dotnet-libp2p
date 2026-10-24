# Windows process-key recovery (#402)

Stacked on PR #416, targeting `feature/20261023-212739-3e2248`; preserves the parent fixes and is limited to #402.

Closes #402

The parent at `f94d0d071518dd43085303f6f24b5de7b2660e82` already creates one
named P-256 key per loaded helper, rather than per connection. Both registered
TLS roles and QUIC dial/listen call `CertificateHelper.CreateCertificateLease`.
Normal process exit deletes the process-owned key; individual certificates own
independently disposable handles. Abrupt termination bypasses that cleanup.
The parent has no recovery path. The [parent validation record](secure-transport-validation.md)
retains its actual Windows transport and normal-exit evidence.

## Ownership and recovery

New keys use the current-user Microsoft Software Key Storage Provider, the
parent's algorithm, export policy and usage, and a fresh random name
`libp2p-process-v1-{nonce:N}`. Overwrite is disabled. No process adopts another
process's private key. The process boundary, including the parent's multiple
assembly-load-context limitation, is preserved.

The custom property `libp2p-process-owner` is exactly 40 bytes:

| Offset | Encoding | Value |
| --- | --- | --- |
| 0 | 8 bytes | ASCII `libp2p` followed by two zero bytes |
| 8 | little-endian Int32 | Version 1 |
| 12 | little-endian Int32 | Positive process ID |
| 16 | little-endian Int64 | Positive raw process creation FILETIME |
| 24 | 16 bytes, .NET Guid byte order | Nonempty nonce matching the complete key name |

`CngKeyCreationParameters.Parameters` supplies this property with
`CustomProperty | Persist`. [.NET 10.0.12 creation source](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/CngKey.Create.cs)
sets properties before finalization and closes failed creation handles. No
post-creation metadata write or sidecar file creates an ownership gap.

Once, before lazy process-key creation, recovery tries a current-user,
cross-session mutex without waiting. [.NET's named wait handle options](https://learn.microsoft.com/en-us/dotnet/api/system.threading.namedwaithandleoptions?view=net-10.0)
provide this scope. Contention or inaccessible synchronization skips recovery;
marked creation can continue. Creation never holds the mutex. Recovery remains
synchronous so release happens on the acquiring thread. Abandonment grants
exclusion only; every candidate is revalidated.

[Provider enumeration](https://learn.microsoft.com/en-us/windows/win32/api/ncrypt/nf-ncrypt-ncryptenumkeys)
selects candidates by name, then checks provider, user store, algorithm, exact
property format/version and nonce correspondence. A retained process handle
establishes creation time and termination state. An existing matching incarnation whose process object has not signaled is live. A signaled matching process, a different creation time, or
`OpenProcess`'s invalid-parameter result for a validated positive PID proves the
original incarnation dead. Access denial, failed queries, invalid creation times
and unexpected wait results are unknown and retain the key.

Only a verified dead owner's key is deleted, through the same verified key
handle. Legacy `libp2p-*` keys, independent keys, missing or malformed properties,
future versions, mismatched nonces, live owners and unknown liveness are retained.
Deletion failure leaves ownership evidence for a later process to retry.
Recovery has no timer, background worker, public setting or transport-disposal
dependency. Normal exit retains the parent's exact-handle cleanup without waiting.

Recovery is opportunistic: contention, access restrictions or deletion failures
can defer cleanup until another initialization. Historical unmarked keys cannot
be reclaimed automatically. This change does not promise zero residual keys in
those conditions, change TLS wire behavior, or resolve certificate-list issue #373.

## Creation feasibility evidence (host run, before this candidate)

The approved plan records a host-run creation-proof bundle with source hashes,
platform metadata, exact commands and exact-key cleanup records.
On Windows 10.0.26200, runtime 10.0.12, SDK 10.0.401, the host ran
`dotnet run --project cng-creation-probe/Probe.csproj -c Release -p:TreatWarningsAsErrors=true`.
All five cases passed: kill after native creation, kill after property persistence,
kill after finalization, managed custom-property persistence and invalid managed
property initialization. Only the finalized control survived; it retained exact
ownership evidence. The bundle includes source hashes and exact-key cleanup.
This is preliminary host evidence, not execution of this candidate or proof of
every Windows interruption point.

The retained tests port all five checks, extend managed creation to close/reopen,
and fail if *any* enumerated survivor cannot be opened and verified. A failure to
read an enumerated key is never treated as absence. Child barriers acknowledge
native stages before real termination. The recovery tests also cover repeated
crash/restart, live signing, distinct private keys, concurrent initializations,
lock contention, recoverer termination/abandonment, unavailable synchronization,
strict ownership exclusions, unknown liveness and failed-deletion retry. Pure
classification tests cover PID reuse, access denial, query failures and unexpected
native results; real process-handle tests complement those deterministic seams.
Test cleanup is restricted to acknowledged unique test keys after child exit,
with public-key fingerprints checked when available.

## Corrected Windows validation and replay

The host executed `9314cce7e980e028534629f0468d5a01659f1b44` on 2026-10-24,
03:46:10–03:47:21 UTC, using Windows 10.0.26200 (win-x64), SDK 10.0.401 and
runtime 10.0.12. The immutable `9314cce7-corrected` snapshot contains exact
commands, platform metadata, source/test/CI SHA-256 hashes, full logs, NUnit XML,
a summary and a read-only cleanup observation. All 461 recorded input hashes
match the retained candidate. The three commands exited 0, with builds treating
warnings as errors. No Windows execution took place on the Linux worker.

| Corrected Windows suite | Passed / executed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| QUIC, ownership and recovery | 74 / 74 | 0 | 0 |
| TLS | 32 / 32 | 0 | 0 |
| Registered transport/recovery/alias controls | 7 / 7 | 0 | 0 |
| Total | 113 / 113 | 0 | 0 |

The QUIC XML includes all 17 `WindowsProcessKeyRecoveryTests` cases and excludes
only their `ChildProcess` entry point. All five creation checks passed, including
ownership inspection of enumerated survivors and managed close/reopen. The
crash/restart test passed all three cycles, proving death of the exact owner,
persistence before recovery, orphan removal and live-owner survival. The
contention/abandonment case passed its acknowledged lock barriers and successor
recovery checks. The independent worker-handle termination check also passed.

The seven registered cases comprise Noise/TLS/QUIC failure/retry and application
traffic, both expected-peer alias controls, and both Windows TLS/QUIC crash/restart
cases. They validate recovery through the registered builders while a separate
live peer remains usable, mutual authentication, expected-peer rejection,
successful retry, collection and bidirectional application traffic. Per-test
owned-key/process cleanup assertions passed. The cleanup observation was read
only and performed no broad process or key cleanup.

These are results for the exact tested revision above, not execution of a later
documentation commit. This documentation update changes no production, test,
dependency, build or CI input, so the matching Windows evidence can be reused.
Any subsequent Windows-sensitive input change requires fresh matching results.
Independent re-review and final verification remain publication gates. These
runs do not establish external-client interoperability or resolve #373.

### Initial failed run and corrected test boundaries

The first candidate, `7a70772074ed1cb071eab78e3b09f49c9c0123d1`, was executed by
the host on Windows 10.0.26200 / runtime 10.0.12 / SDK 10.0.401. The immutable
results snapshot includes NUnit XML, command logs, input hashes and a read-only
process cleanup observation. All 461 recorded input hashes matched the candidate
before revision. No Windows execution took place on the Linux implementation host.

| Initial Windows suite | Passed / executed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| QUIC, including all 17 recovery cases | 71 / 73 | 2 | 0 |
| TLS | 32 / 32 | 0 | 0 |
| Registered transport/recovery/alias controls | 7 / 7 | 0 | 0 |

The initial QUIC run excluded the `ChildProcess` entry point. This failed
snapshot remains historical. Both following cases passed in the corrected run:

- `CrashRestartRecoversOnlyTheDeadIncarnation`: immediately after the harness's
  kill/wait returned, the recorded owner was still live by the kernel-handle
  check. [.NET 10.0.12 process status](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Windows.cs#L259-L289)
  accepts an exit code before checking whether the process object has signaled;
  [WaitForExitAsync](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.cs#L1367-L1469)
  can take that path. The harness now uses bounded `WaitForExit(int)`, which
  [waits on the retained process handle](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs#L149-L171).
  Every stage reports its actual worker PID and, on Windows, exact creation
  FILETIME. The harness validates these against the process it launched; CNG
  ownership reports must match that same incarnation. It terminates only that
  retained process and independently tests that the worker handle has signaled.
  A launcher/worker mismatch now fails explicitly; the original logs did not
  record enough identity information to establish such a mismatch.
- `RecoverySkipsContentionAndRevalidatesAfterAbandonment`: the orphan was absent
  despite the contender correctly skipping the held mutex. NUnit enumerated the
  assembly's certificate test source, which called the production helper and
  performed recovery before the selected holder test acquired its mutex. Test
  cases now defer certificate construction until execution, preserving every
  vector, case name and expected result. The holder also acknowledges acquisition
  while the future orphan is live; the test then kills that exact owner and
  proves death and orphan presence before starting contenders. Contention,
  abandonment, successor recovery and live-owner assertions remain enforced.

The production ownership/recovery implementation and every CI minimum were
unchanged by these test-harness corrections. The corrected snapshot validates
the revised test inputs; the initial snapshot must not be reused for them.

### Portable replay commands

Use the repository's Microsoft.Testing.Platform runner. The existing full Windows
minimums remain QUIC 32, TLS 32 and registered controls 5. Added gates require 17
creation/recovery cases and two registered crash/restart cases; child entry-point
methods are excluded from those minimums. Windows skips do not count as execution.
The recorded host run combined the recovery cases into the full QUIC suite and
both registered recovery cases into the registered controls. Replay its commands
from repository root, with `TEMP`/`TMP` configured by the host. Only the private
settings/output paths are replaced below; filters, timeouts and execution floors
match the recorded invocation. The QUIC floor was 49; 74 cases actually executed.
No CI minimum was reduced.

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:TEMP/secure-transport-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:TEMP/secure-transports.xml"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 49 --timeout 5m --settings "$env:TEMP/secure-transports.xml" --filter "FullyQualifiedName!~ChildProcess"
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 5m --settings "$env:TEMP/secure-transports.xml"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 7 --timeout 5m --settings "$env:TEMP/secure-transports.xml" --filter "(FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId|FullyQualifiedName~RegisteredWindowsKeyRecoveryTests)&FullyQualifiedName!~ChildListener"
```

For red-before-green Windows recovery evidence, the host can disable only the
`WindowsProcessKey.Recover()` call in `CreateWindowsProcessKey` in a disposable
candidate copy and run `CrashRestartRecoversOnlyTheDeadIncarnation`. It must fail
at the orphan-removal assertion, after observing the exact acknowledged orphan
survive its owner's termination. Restore the call and rerun. A pinned parent
build independently demonstrates its unmarked residual; the candidate must retain
that artifact. Do not move this checkout's HEAD or reuse a candidate marker as a
parent-created key. Windows candidate evidence and independent review remain
host publication gates.

## Initial local validation record

Implementation used base `f94d0d071518dd43085303f6f24b5de7b2660e82` without
moving HEAD or committing. Linux x64, SDK 10.0.402, runtime 10.0.13:

| Check | Executed outcome | Windows-only skips |
| --- | --- | ---: |
| Full QUIC | 53 passed, 0 failed | 21 |
| Full TLS | 32 passed, 0 failed | 0 |
| Registered controls plus new Windows fixture | 5 passed, 0 failed | 3 |
| Recovery gate filter, minimum 17 | 17 selected; exit 8, zero executed | 17 |
| Registered recovery gate filter, minimum 2 | 2 selected; exit 8, zero executed | 2 |
| Folder whitespace verification | Exit 0 | n/a |
| cspell, all changed files | Exit 0, no issues | n/a |

From repository root, all test builds used warnings as errors and one configured
temporary artifact root (Debug configuration):

```sh
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 53
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId|FullyQualifiedName~RegisteredWindowsKeyRecoveryTests' --minimum-expected-tests 5
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes --verbosity diagnostic
```

The native gate checks used the two new CI filters and minimums with `--no-build`
and the same artifact root. Their expected failures confirm that skipped native
cases cannot satisfy acceptance. A reversible fault injection accepting ownership
version 2 caused `UnprovenOwnershipIsRejected("future")` to fail (11 passed,
1 failed, exit 2); restoring the guard returned the full suite to green. This
validates the ownership guard, not native Windows crash recovery. Earlier compiler
and spelling failures were corrected before the final checks. Candidate Windows
crash reproduction, recovery, native ABI and registered-path execution were
pending the host at that point; the failed initial Windows run is recorded above.
The preliminary creation probe remains separate from candidate validation.

## Revision validation

The correction changes only the isolated-process harness, certificate test data,
process-key tests and this record. Linux x64, SDK 10.0.402, runtime 10.0.13:

| Check | Executed outcome | Windows-only skips |
| --- | --- | ---: |
| Focused harness tests, Debug | 2 passed | 0 |
| Full QUIC, Release | 54 passed | 21 |
| Full TLS, Release | 32 passed | 0 |
| Registered controls/recovery fixture, Release | 5 passed | 3 |

All builds treat warnings as errors. The first harness attempt exposed the Unix
runtime's estimated wall-clock `Process.StartTime`, which differs between process
instances. Exact creation-time comparison is therefore restricted to Windows's
raw FILETIME; PID acknowledgement and retained-process termination checks run on
both platforms. No Windows ownership or liveness assertion was relaxed.

From repository root, using one configured temporary artifact root:

```sh
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~Isolated' --minimum-expected-tests 2
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 54
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId|FullyQualifiedName~RegisteredWindowsKeyRecoveryTests' --minimum-expected-tests 5
```

The subsequent corrected Windows run at
`9314cce7e980e028534629f0468d5a01659f1b44`, recorded above, establishes the native
results that these Linux skips could not. Both previously failing cases passed.
No production/test/CI inputs changed while incorporating that evidence; independent
re-review and final verification remain required. The implementer performed no
commits or publication.
