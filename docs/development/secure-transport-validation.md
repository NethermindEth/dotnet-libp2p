<!-- cspell:words runsettings -->

# Secure transport validation

This is the historical PR #416 record for its native TLS backend, not validation
of the combined PR #424 tree. The [PR #424 record](tls-cardinality-validation.md)
describes the managed backend that supersedes the certificate-list limitation
recorded here. All results below retain their original revision scope.

PR #416 now targets `main` and incorporates PR #411 through merge
`29ec47562587b3bc87cdc0a3e9bd5cfeae72e053`. This record was refreshed on
2026-10-30 against candidate `53a2456b94551eccf74a1fdbef231e38b7ddf96d`,
which preserves the incoming main changes through `34963ed`, including the TCP
and QUIC receive-buffer corrections from PR #406.
The pinned comparison base is `34963edfd699234953e34efc33f23f3bc3764767`;
the candidate differs from that pinned base in 14 files. These documentation
updates preserve the candidate's production code, tests, and CI configuration.

The original stacked parent was `0e731cc8b84ad51fd7af3bc31e58744678e0a427`,
on `feature/20261023-122715-498974`. References to that parent below describe the
original regression baseline, rather than the current PR target. Its identity
canonicalization, certificate self-signatures, expected peer checks, and
registered protocol stack are preserved.

## Noise framing (#371)

The [Noise specification, framing section](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/noise/README.md#the-wire-format)
uses an unsigned two-byte big-endian length with a 65,535-byte message maximum.
The pinned Noise.NET 1.0.0 dependency's `Protocol.MaxMessageLength` equals
`ushort.MaxValue`; the regression asserts this correspondence. No two-byte prefix
can exceed that bound. Rejecting the high bit would reject legal messages.
The [Go implementation](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/noise/handshake.go)
also writes unsigned lengths.

`HandshakeFramingTests` exercises the real `DialAsync` and `ListenAsync` methods
with an independent Noise handshake state and the production `Channel`:

- All three reads accept `0x7fff`, `0x8000`, and `0xffff`.
- Authenticated handshake payloads include legal unknown protobuf extension data.
- Fragmented prefixes and bodies consume precisely their frame. A coalesced next
  encrypted record survives authentication and reaches the upgraded channel.
- Truncated prefixes and bodies raise `ChannelClosedException` without publishing
  a remote identity or upgrading. This already has routine-failure classification;
  the Core logging policy needs no change.

Against the unchanged parent, the 18 new cases discovered 12 failures and six
passes. The failures reached each of the three signed reads and reported negative
lengths (`-32768` and `-1`). The original framing revision passed 38 Noise cases;
the current candidate passes 40, including the tests incorporated from PR #411.
Writes now narrow with checked unsigned conversions, and the responder reserves
two additional bytes for its prefix.

The responder's `MaxMessageLength + 2` output capacity is documented rather than
test-pinned. The locally constructed response is small, so the current tests do
not reach its output-buffer boundary. The large incoming-frame regressions above
exercise the unsigned reads, not this separate capacity property.

### Separate transport-record follow-up

The post-handshake receive loop has a separate short-record allocation and
exception-propagation defect. The [follow-up and reproduction](noise-transport-follow-up.md)
record the verified behavior, now tracked in
[issue #431](https://github.com/NethermindEth/dotnet-libp2p/issues/431).
PR #416 does not fix or close that separate issue.

## Received TLS certificate lists (#373, unresolved)

The [TLS peer-authentication specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/tls/tls.md#peer-authentication)
requires rejecting multiple **transmitted** certificates, including duplicate
entries. The [Go validator](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto.go)
checks the length of the list parsed from the TLS callback's raw certificates.
Its [reference tests at the same revision](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto_test.go)
were inspected, not executed here; this is not a claim of external-client interoperability.

`CertificateListProbeTests` uses Bouncy Castle 2.6.2 as a TLS 1.3 sender in both
directions. It captures and parses the final Certificate serialization hook and
then invokes the original sender. The
[pinned sender implementation](https://github.com/bcgit/bc-csharp/blob/release-2.6.2/crypto/src/tls/TlsProtocol.cs)
encodes that same certificate directly into the handshake message, without chain
building or entry filtering. The probe compares exact DER entries with the
expected emitted list, checks the callback leaf, verifies the leaf self-signature
and expected libp2p identity, and exchanges an application byte. It does not infer
emission from `SslStreamCertificateContext` or a configured collection.

Observed on Linux, .NET 10.0.13, OpenSSL 3.0.2 (six probe cases passed):

| Receiver | Transmitted list | ChainElements | ExtraStore | Leaf copies in ExtraStore |
| --- | --- | ---: | ---: | ---: |
| Client | Leaf | 1 | 1 | 1 |
| Client | Leaf + unrelated | 1 | 2 | 1 |
| Client | Leaf + duplicate | 1 | 2 | 2 |
| Server | Leaf | 1 | 0 | 0 |
| Server | Leaf + unrelated | 1 | 1 | 0 |
| Server | Leaf + duplicate | 1 | 1 | 1 |

Thus `ChainElements.Count == 1` accepts all three lists. Linux's `ExtraStore`
retains sufficient information in this probe, with different leaf inclusion by
direction. It is not a portable exact-list API:

- [.NET 10.0.13 Unix extraction](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Security/src/System/Net/CertificateValidationPal.Unix.cs)
  copies the OpenSSL peer chain into `ExtraStore`.
- [Windows extraction](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Security/src/System/Net/CertificateValidationPal.Windows.cs)
  obtains `ExtraStore` through a certificate store. The
  [store enumerator](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/Common/src/Interop/Windows/SChannel/UnmanagedCertificateContext.IntPtr.cs)
  enumerates contexts and excludes the leaf context; it does not expose the wire
  list or its multiplicity.
- The [public ExtraStore contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.extrastore?view=net-10.0)
  describes inputs for chain building, without guaranteeing exact wire entries.

The host also ran the same six probes on Windows 10.0.26200, .NET 10.0.12, at
`84e0e2237eb5c8ce88ba59b747950f6a7cd2aa52`. All six passed. Their NUnit XML records:

| Receiver | Transmitted list | ChainElements | ExtraStore | Leaf copies in ExtraStore |
| --- | --- | ---: | ---: | ---: |
| Client | Leaf | 1 | 0 | 0 |
| Client | Leaf + unrelated | 1 | 1 | 0 |
| Client | Leaf + duplicate | 1 | 0 | 0 |
| Server | Leaf | 1 | 0 | 0 |
| Server | Leaf + unrelated | 1 | 1 | 0 |
| Server | Leaf + duplicate | 1 | 0 | 0 |

Windows drops the duplicate leaf from `ExtraStore` in both roles. In both the
two-entry duplicate case and the valid one-entry control, the callback exposes
only that case's presented leaf: one chain element and an empty `ExtraStore`.
The emitted DER list is checked separately by the sender probe. Duplication adds
no observable certificate entry. This is a concrete loss of wire multiplicity,
not merely an undocumented API guarantee. Neither collection can enforce the
exact transmitted-list requirement on this platform/runtime.

No portable cardinality enforcement is claimed, and no chain-count substitute
was added. The unsupported full-conformance claim was removed from the TLS
README. #373 remains unresolved and must not have a closing reference in the PR.
The existing Windows TLS job continues to run the six probes and upload their
NUnit XML observations.

The platform-conditioned `ExtraStore` assertions deliberately pin the observed
OpenSSL and Schannel extraction behavior, including exact DER entries and
duplicate multiplicity. A platform update can therefore fail these probes
without a repository regression. For such a failure, compare the logged OS and
runtime, actual serialized list, callback leaf, and callback collections with the
tables above. Reproduce both roles and all three list shapes before updating an
expectation; a changed platform observation must remain distinct from an error
in certificate serialization, identity validation, or the probe itself.

## Session-key lifetime (#402, partial mitigation)

The parent creates a fresh persisted named Windows CNG key on every helper call;
both registered TLS roles and QUIC dial/listen use this helper. Only lease disposal
deletes it. This source trace confirms the crash-leak premise; the audit's Windows
GC and timing experiment was not independently executed on this Linux host.

Independent review F1 against `0aa5b634f6803be8309a696b9a73189ecffb592e` correctly
identified that the initial in-memory replacement breaks the Windows credential
path. This is source-confirmed, not a Windows execution result:

- [.NET 10.0.13 SslStream credentials](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Security/src/System/Net/Security/SslStreamPal.Windows.cs#L201-L238)
  translate the native missing-credentials error for an ephemeral certificate key
  into an authentication failure.
- [MsQuic credential configuration](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Quic/src/System/Net/Quic/Internal/MsQuicConfiguration.cs#L220-L273)
  uses the certificate context on Schannel and likewise rejects ephemeral keys.
- [Windows certificate contexts](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Security/src/System/Net/Security/SslStreamCertificateContext.Windows.cs#L14-L18)
  do not turn that key into a named one.

The revision restores a named P-256 CNG key with the parent's provider settings,
shared by all certificates for the lifetime of the loaded helper. In a normal
application load context, this is one key per process, created lazily. Further
connection attempts, protocol instances, identities, and public helper calls do
not create additional persisted keys. Certificate creation is serialized around
the shared signing object. The process retains ownership across individual lease
disposal, input-key disposal, and native TLS/QUIC credential caching. Each returned
certificate still owns its own certificate/private-key handle. Callers dispose
that certificate and must not explicitly delete the shared key. Outside Windows,
the supplied session key remains in use as before.

Process ownership avoids introducing protocol-disposal assumptions: the registered
builder can create protocols with `ActivatorUtilities.GetServiceOrCreateInstance`,
so a new `IDisposable` on those protocols alone would not establish DI ownership.
The public helper also supports callers independent of registered protocols.

On normal process exit, cleanup attempts deletion using the exact retained key
handle created by this process. It does not wait on a signing thread during process
termination; a busy owner has the same residual as failed shutdown cleanup. No name search, PID inference, recovery database,
startup sweep, or deletion of legacy keys is performed. If key-wrapper construction
fails, that newly created key is deleted before returning the error. The lazy
owner also prevents repeated failed attempts from creating more keys.

**Residual crash leak:** abrupt termination or failed shutdown deletion can leave
one persisted key per loaded helper per process run. Across N such runs, up to N
keys remain; this is unbounded across repeated restarts. The revision removes
per-connection key churn, not this residual. Multiple separately loaded helper
assemblies each own one key. The Windows transport results below validate the
compatible reuse and normal-exit paths; they do not eliminate this crash leak.
#402 remains partial and must not have a closing reference in the PR.

`WindowsCertificateKeyTests` covers sequential and concurrent certificate leases
with distinct input keys, shared key-name/public-key fingerprints, independent
certificate disposal, and a surviving certificate that still signs successfully.
Its subprocess case runs a real child test process, checks that normal exit
deletes the child's exact key, and verifies that the active parent's different key
remains present. Failed-probe cleanup requires child exit and the reported key's
public-key fingerprint before deleting that exact key. The certificate-list probe
no longer deletes private keys borrowed from the shared process owner.

`RegisteredSecureTransportTests` continues to use `AddLibp2p`, the existing
TLS-selecting builder, and `WithQuic` to exercise three fresh peers per transport.
Each performs a failed expected-peer handshake followed by successful mutual
authentication, forced collection, and application requests in both directions.
The existing QUIC tests also reject a missing client certificate before a
successful dial. TLS disposes its native stream before the certificate lease on
both success and authentication failure.

The TLS disposal order is documented rather than test-pinned: both role methods
explain why the lease must be declared before the `using SslStream`. The current
TLS and E2E assertions do not detect removal of either stream's `using` or a
reversal of the declaration order. No ordering-test coverage is claimed.

The Windows job keeps the full TLS/QUIC suites, requires at least 32 executed QUIC
cases, and retains the registered transport tests. The historical Windows run
below confirms handshake, concurrency, certificate lifetime, and normal-exit
cleanup at its recorded revision. The busy `Monitor.TryEnter` shutdown path has
source inspection only; the normal-exit test does not exercise that branch.
Neither Linux results nor the historical snapshot establish current Windows
assertion coverage. The CI gates remain intact.

## Recorded Windows validation

A clean checkout of `84e0e2237eb5c8ce88ba59b747950f6a7cd2aa52` was tested on
2026-10-23, from 21:26:22 to 21:26:56 UTC. The
[portable results snapshot](evidence/windows-secure-transport-results.json)
records the environment, exit codes, suite totals, all executed case names, and
six certificate-list observations extracted from the supplied NUnit reports.
It also includes the original reports' SHA-256 hashes. Machine/user names and
absolute paths are omitted. This snapshot is a summary, not the raw logs, and
the recorded run is not presented as a published GitHub Actions run.

The [Test workflow](../../.github/workflows/test.yml)'s **Windows QUIC and TLS**
job publishes the `windows-secure-transport-results` artifact. For a completed
run of the revision under inspection, download that artifact to inspect the TLS
NUnit XML, including the six certificate-list probes. QUIC and registered test
results are in the same job's step logs. The snapshot above keeps the recorded
observations available in the repository without depending on private machines
or CI artifact retention.

- OS: Microsoft Windows NT 10.0.26200.0, win-x64.
- SDK: 10.0.401; MSBuild: 18.9.11; runtime: 10.0.12.
- All three commands exited 0. All 69 recorded cases passed; no case was skipped,
  failed, or inconclusive.

| Suite | Passed / executed | Skipped |
| --- | ---: | ---: |
| QUIC | 32 / 32 | 0 |
| TLS | 32 / 32 | 0 |
| Registered transport and alias controls | 5 / 5 | 0 |

The QUIC report includes both sequential/concurrent process-key reuse cases and
`NormalProcessExitDeletesOnlyItsOwnKey`, all passed. The TLS report includes all
six certificate-list probes. The registered report includes Noise, TLS, and QUIC
failure/retry and bidirectional application exchange, plus both expected-peer
alias controls. Thus the named-key correction to review F1 is validated through
the registered Windows production paths as well as the key-lifetime tests.

To repeat these checks on a Windows Actions runner, run the following from the
repository root. `RUNNER_TEMP` is provided by Actions. The commands create their
own settings file using the same XML output configuration as the workflow; no
settings file from the recorded machine is needed. Paths differ from the
original invocation, while the test options and registered filter are preserved.

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:RUNNER_TEMP/secure-transport-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:RUNNER_TEMP/secure-transports.xml"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 180s --settings "$env:RUNNER_TEMP/secure-transports.xml"
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 180s --settings "$env:RUNNER_TEMP/secure-transports.xml"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 5 --timeout 180s --settings "$env:RUNNER_TEMP/secure-transports.xml" --filter "FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId"
```

These historical results resolved the execution-evidence blocker at their
recorded revision. They do not establish cross-client interoperability or
hard-crash orphan recovery, and they do not turn the #373 visibility probe into
protocol enforcement. The later Windows `ExtraStore` assertions were not present
in that run.

### Current candidate Windows evidence

On 2026-10-30, authenticated `gh` reads retrieved and independently inspected the
raw [Windows job log](https://github.com/NethermindEth/dotnet-libp2p/actions/runs/41061514588/job/124082159814)
and [TLS NUnit artifact](https://github.com/NethermindEth/dotnet-libp2p/actions/runs/41061514588/artifacts/12979936070)
from run `41061514588`, whose PR head is
`53a2456b94551eccf74a1fdbef231e38b7ddf96d`. The actual checkout was CI merge
`9621c796dce7cdac2af6e3e755fe755504787222`, with that head and main revision
`34963edfd699234953e34efc33f23f3bc3764767` as parents.

The checkout's complete Git tree is
`9e41e6303bb10ed4a483b45a2e0c4a155cb07121`, exactly equal to the candidate's
`HEAD^{tree}`. Thus every tracked source, test, fixture, build setting, and
workflow matches the supplied candidate, including the incoming TCP and QUIC
receive-buffer changes. The commit identities differ; these remain CI merge
results with the candidate's exact tracked content. This documentation correction
is the only subsequent worktree change.

The Windows job and all its build/test/upload steps completed successfully. The
wider Test workflow's Linux job was still running when inspected, so this is not
a claim that the entire workflow completed. The Windows runner used Windows
10.0.26100 x64, SDK 10.0.403, and runtime 10.0.14. Raw step summaries show:

| Scope | Passed / total | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Full QUIC suite | 42 / 42 | 0 | 0 |
| Full TLS suite | 32 / 32 | 0 | 0 |
| Registered transport and alias controls | 5 / 5 | 0 | 0 |

The unfiltered QUIC invocation executed all three Windows key-reuse and
normal-exit cases, as well as the nine incoming receive-buffer cases, without
skips. The non-Windows `Assert.Ignore` guards restrict native CNG tests to
Windows; they remain appropriate on Linux. The TLS XML independently records
32 passing case nodes, including all six
`ReportsWireCertificateListAndCallbackContents` combinations. Each probe has
`ChainElements=1` and `leafCopies=0`; in both roles, `ExtraStore` is 0 for the
leaf, 1 for the unrelated extra certificate, and 0 for the duplicate leaf. Both
receiver roles and all three list shapes passed the platform-conditioned length
and DER-entry assertions. This verifies actual cases beyond job metadata.

The TLS report's SHA-256 is
`5835592f4296723dbe64cb0041d94a5056cebca221e3986cb2cda17d7fd88bc7`.
The raw Windows job log's SHA-256 is
`20b790c1a7f5641a5db86eda2759154a2eff8c8f35091b26eaaaf754652cd604`.
Raw files are session artifacts. The linked Actions artifact contains the TLS
XML; QUIC and registered totals come from the linked job log. Retrieval succeeded
for this run, independently of the earlier snapshot and earlier egress failures.

These results verify normal Windows execution of the supplied transport code.
They do not exercise a busy `Monitor.TryEnter` shutdown, pin TLS declaration
order or responder buffer capacity with mutation tests, establish cross-client
interoperability, or demonstrate hard-crash orphan recovery. Those coverage
limits remain explicit.

Read-only retrieval commands, with output under the session's `TMPDIR`:

```sh
gh run view 41061514588 --repo NethermindEth/dotnet-libp2p --json headSha,status,conclusion,jobs,url
gh run download 41061514588 --repo NethermindEth/dotnet-libp2p --name windows-secure-transport-results --dir "$TMPDIR/windows41061514588"
gh api --allow-escape-sequences repos/NethermindEth/dotnet-libp2p/actions/jobs/124082159814/logs > "$TMPDIR/windows41061514588.log"
gh api repos/NethermindEth/dotnet-libp2p/commits/9621c79
```

The API log command writes terminal control bytes to a file for parsing; those
bytes are stripped before inspecting the summaries. No global settings change.

#### Earlier Windows evidence

Previously inspected run `41053009544`, with PR head `4a0094c` and CI merge
`eecbbaae5dc73e5915437d6f8786e951e0fb8a5d`, passed QUIC 33/33, TLS 32/32,
and registered controls 5/5. Its TLS XML and job log hashes were respectively
`b1c20bfa3b927ffdf0fa02de4736809dce3a9cd503e77a8dd9ab1b9378dbd076` and
`455226113c6739ed016a42a0d903ed21614f0ba6f207a1a57c16befe2b70058c`.
Its complete tree differed from `23b93fa` only in this document; it predates
PR #406's receive-buffer changes. These are historical results, superseded for
the supplied candidate by the independently inspected run above.

## Current candidate Linux validation

Fresh Release checks on 2026-10-30 used candidate
`53a2456b94551eccf74a1fdbef231e38b7ddf96d`, SDK 10.0.402, and runtime
10.0.13 on Linux. This host supports QUIC. All builds used
`-p:TreatWarningsAsErrors=true` and one shared artifact root under `TMPDIR`.
The checks were rerun after the incoming main updates. Only this validation
document changed afterward, so the successful results are reused for this
documentation correction.

| Scope | Passed | Skipped | Total |
| --- | ---: | ---: | ---: |
| TCP | 24 | 0 | 24 |
| Noise | 40 | 0 | 40 |
| TLS, including all six certificate-list probes | 32 | 0 | 32 |
| QUIC | 39 | 3 | 42 |
| Registered identity and secure-transport tests | 45 | 0 | 45 |

The three QUIC skips are the Windows-only key-lifetime cases. XML inspection
confirmed all 14 incoming TCP and nine incoming QUIC receive-buffer cases ran,
including cancellation, failure, EOF, compact retained fragments, and exactly-once
pool returns. All 18 Noise framing regressions, six TLS certificate-list probes,
and three registered secure transports executed. The full E2E suite was not
rerun; the 45 cases above are the explicitly filtered production-stack checks.

Commands ran from the repository root. The shared runner settings selected an
NUnit output directory under the same session's `TMPDIR`. Each test command was
wrapped with `timeout --signal=TERM --kill-after=10s 240s`:

```sh
dotnet test --project libp2p/Libp2p.Protocols.IpTcp.Tests/Libp2p.Protocols.IpTcp.Tests.csproj -c Release --artifacts-path "$TMPDIR/a" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 14 --timeout 180s --settings "$TMPDIR/secure-transports.runsettings"
dotnet test --project libp2p/Libp2p.Protocols.Noise.Tests/Libp2p.Protocols.Noise.Tests.csproj -c Release --artifacts-path "$TMPDIR/a" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 40 --timeout 180s --settings "$TMPDIR/secure-transports.runsettings"
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/a" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 180s --settings "$TMPDIR/secure-transports.runsettings"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release --artifacts-path "$TMPDIR/a" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 25 --timeout 180s --settings "$TMPDIR/secure-transports.runsettings"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/a" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 45 --timeout 180s --settings "$TMPDIR/secure-transports.runsettings" --filter 'FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredIdentityTests'
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes
npx --no-install cspell --no-progress -c cspell.json docs/development/noise-transport-follow-up.md docs/development/secure-transport-validation.md
```

All five test commands exited 0: 180 cases passed and three were skipped. The
QUIC minimum of 25 permits the established Linux skips on workers without QUIC
support; the observed result here was 39 executed cases. The Windows CI execution minimum remains 32. No CI gate changed.

### Historical results

The earlier Linux check at `23b93fa` passed Noise 40, TLS 32, QUIC 30 with three
Windows-only skips, and registered tests 45. It predates the incoming TCP/QUIC
receive-buffer changes and is not the current candidate's result.

The initial parent Noise baseline passed 20 tests. Retained Linux Release checks
at `84e0e2237eb5c8ce88ba59b747950f6a7cd2aa52` passed Noise 38/38, TLS 32/32,
QUIC 29 plus three Windows-only skips, and registered E2E 45/45. The Core identity
tests passed 75/75 during the initial implementation. These are historical
measurements, not the current candidate's test counts.

## PR handoff

The only closing reference is:

```text
Closes #371
```

#373 remains unresolved because the Windows callback loses transmitted duplicate
entries. #402 is a partial mitigation of per-attempt key creation and retains one
possible orphan per crashed process run. Neither issue has a closing reference.
The published PR body targets main, but authenticated inspection on 2026-10-30
still found a stale `37bfd9e` head statement and older Windows evidence. A
complete corrected description is returned through the host's disposition
protocol, with validation pinned to its actual revisions and no moving-head
claim. The host owns publication and the fresh audit afterward. Prepared
publication text is kept outside the source checkout; this validation record
is not a draft PR description. The separate Noise issue #431 remains open and
outside this PR's runtime changes.
