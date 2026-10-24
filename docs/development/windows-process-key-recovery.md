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

The approved plan records the read-only creation-proof bundle at
`/root/hammer-workspace/implementation/audit1-20261023/cng-creation-proof`.
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

## Candidate validation and host replay

No candidate Windows execution is claimed on Linux. Host results must record the
candidate revision, production/test/CI hashes, OS/SDK/runtime, exact commands,
executed names/counts/skips, outcomes and exact-key cleanup records. The authorized
results inbox is
`/root/hammer-workspace/implementation/audit1-20261023/windows-key-validation`.
It was absent during implementation. Compare all relevant hashes before reusing
results; changed Windows-sensitive inputs require fresh host validation.

Use the repository's Microsoft.Testing.Platform runner. The existing full Windows
minimums remain QUIC 32, TLS 32 and registered controls 5. Added gates require 17
creation/recovery cases and two registered crash/restart cases; child entry-point
methods are excluded from those minimums. Windows skips do not count as execution.
Run from `libp2p`, with `TEMP`/`TMP` configured by the host:

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:TEMP/secure-transport-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:TEMP/secure-transports.xml"
dotnet test --project Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --settings "$env:TEMP/secure-transports.xml"
dotnet test --project Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~WindowsProcessKeyRecoveryTests&FullyQualifiedName!~ChildProcess" --minimum-expected-tests 17 --settings "$env:TEMP/secure-transports.xml"
dotnet test --project Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --settings "$env:TEMP/secure-transports.xml"
dotnet test --project Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId" --minimum-expected-tests 5
dotnet test --project Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~RegisteredWindowsKeyRecoveryTests&FullyQualifiedName!~ChildListener" --minimum-expected-tests 2 --settings "$env:TEMP/secure-transports.xml"
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

## Local validation record

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
crash reproduction, recovery, native ABI and registered-path execution remain
pending the host; the preliminary creation probe is kept separate above.
