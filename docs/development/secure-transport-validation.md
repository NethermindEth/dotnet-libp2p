<!-- cspell:words runsettings -->

# Secure transport validation

PR #416 now targets `main` and incorporates PR #411 through merge
`29ec47562587b3bc87cdc0a3e9bd5cfeae72e053`. This record was refreshed on
2026-10-30 against candidate `9139341fb475ab715c595b6c155ef35e2d378bf0`.
The pinned comparison base is `5811fe009c6ea2f4a0f7d55e55063381a3cc9c44`;
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
raw [Windows job log](https://github.com/NethermindEth/dotnet-libp2p/actions/runs/41033830821/job/123989712333)
and [TLS NUnit artifact](https://github.com/NethermindEth/dotnet-libp2p/actions/runs/41033830821/artifacts/12966968409)
from run `41033830821`. The run's head is
`9139341fb475ab715c595b6c155ef35e2d378bf0`; its checkout is GitHub's synthetic
merge `c2560ecc2e43866bea929c6f6c4128553f168008`, with that head and main revision
`dd360b27308e1bda07986be4369c10e0da82f05b` as parents. These are CI merge results,
not an execution of the unmerged candidate tree.

The merge includes newer main changes, including an RSA admission guard and
changes to other protocols and Linux CI discovery. Blob comparisons confirmed
that the QUIC certificate helper and protocol, TLS protocol, Windows key tests,
certificate tests, six-case certificate-list probe, identity fixtures, their
project files, shared package/build properties, and SDK settings match the
candidate. The full trees differ; equivalence of every input is not claimed.
The Windows job's test commands are unchanged, and the log confirms the full
suites and registered filter below.

The runner used Windows 10.0.26100 x64, SDK 10.0.403, and runtime 10.0.14. The raw
step summaries show:

| Scope | Passed / total | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Full QUIC suite | 33 / 33 | 0 | 0 |
| Full TLS suite | 32 / 32 | 0 | 0 |
| Registered transport and alias controls | 5 / 5 | 0 | 0 |

The QUIC invocation has no filter; all 33 cases completed without skips, including
the three Windows key-reuse and normal-exit cases. Their source and assertions
match the candidate. The TLS XML independently records 32 passing case nodes and
all six `ReportsWireCertificateListAndCallbackContents` combinations. Each has
`ChainElements=1` and `leafCopies=0`; in both roles, `ExtraStore` is 0 for the leaf,
1 for the unrelated extra certificate, and 0 for the duplicate leaf. All three
list shapes and both receiver roles passed the platform-conditioned length and
DER-entry assertions. This checks actual case results beyond job metadata.

The downloaded TLS report's SHA-256 is
`e9a31ee26a820b629bf85dd42d3776f02abf1a715bdb57046121efa171ac1262`.
The raw Windows job log's SHA-256 is
`ab9b0c72a42dab315265b00b84eb1441b17f9d0eef23aab6114c84a134d4e661`.
Raw files remain session artifacts; the linked Actions artifact contains the TLS
XML, while QUIC and registered totals come from the linked job log.

These results verify normal Windows execution of the PR's transport changes in
the tested merge. They do not exercise a busy `Monitor.TryEnter` shutdown, pin
TLS declaration order or responder buffer capacity with mutation tests, establish
cross-client interoperability, or demonstrate hard-crash orphan recovery. Those
coverage limits remain explicit.

Read-only retrieval commands, with output under the session's `TMPDIR`:

```sh
gh run view 41033830821 --repo NethermindEth/dotnet-libp2p --json headSha,status,conclusion,jobs,url
gh run download 41033830821 --repo NethermindEth/dotnet-libp2p --name windows-secure-transport-results --dir "$TMPDIR/windows41033830821"
gh api --allow-escape-sequences repos/NethermindEth/dotnet-libp2p/actions/jobs/123989712333/logs > "$TMPDIR/windows41033830821.log"
```

The API log command writes terminal control bytes to a file for parsing. It
avoids `gh run view --log`'s unavailable cache directory without changing global
settings; the bytes were stripped before inspecting the displayed summaries.

## Current candidate Linux validation

Fresh Release checks on 2026-10-29 used candidate
`6deef303642830456c3e6f46b9dc40117aea2a8a`, Ubuntu 22.04, SDK 10.0.402, and
runtime 10.0.13. This host supports QUIC. All builds used
`-p:TreatWarningsAsErrors=true` and one shared artifact root under `TMPDIR`.
These Linux results are retained for the inspected candidate because production
and test inputs are unchanged from `6deef30`; they were not rerun for this
documentation refresh.

| Scope | Passed | Skipped | Total |
| --- | ---: | ---: | ---: |
| Noise | 40 | 0 | 40 |
| TLS, including all six certificate-list probes | 32 | 0 | 32 |
| QUIC | 30 | 3 | 33 |
| Registered identity and secure-transport tests | 45 | 0 | 45 |

The three QUIC skips are the Windows-only key-lifetime cases. The reviewer host's
25 passes and eight skips also excluded five cases requiring QUIC support; that
is an environment difference, not a current result from this host. The full E2E
suite was not rerun for this documentation revision; the 45 cases above are the
explicitly filtered production-stack checks.

Commands ran from the repository root. For each suite, the runner settings
selected an NUnit output directory under the same session's `TMPDIR`:

```sh
dotnet test --project libp2p/Libp2p.Protocols.Noise.Tests/Libp2p.Protocols.Noise.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 40 --timeout 180s --settings "$TMPDIR/noise.runsettings"
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 180s --settings "$TMPDIR/tls.runsettings"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 25 --timeout 180s --settings "$TMPDIR/quic.runsettings"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 45 --timeout 180s --settings "$TMPDIR/registered.runsettings" --filter 'FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredIdentityTests'
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes
npx --no-install cspell --no-progress -c cspell.json docs/development/noise-transport-follow-up.md docs/development/secure-transport-validation.md
```

All four test commands exited 0. The QUIC minimum of 25 permits the established
Linux skips on workers without QUIC support; the observed result here was 30
executed cases. The Windows CI execution minimum remains 32. No CI gate changed.

### Historical results

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
The published PR body now targets main and has the refreshed Linux totals, but
on 2026-10-30 its header still called `37bfd9e` the current head. The host owns
publication of the corrected head reference and current Windows evidence.
Prepared publication text is kept outside the source checkout; this validation
record is not a draft PR description. The separate Noise issue #431 remains open
and outside this PR's runtime changes.
