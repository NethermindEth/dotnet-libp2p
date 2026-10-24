# Secure transport validation

Stacked on PR #411, targeting `feature/20261023-122715-498974`. Preserves the parent fixes and limits changes to these secure-transport findings.

The parent revision is `0e731cc8b84ad51fd7af3bc31e58744678e0a427`. Its identity
canonicalization, certificate self-signatures, expected peer checks, and registered
protocol stack are preserved.

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
lengths (`-32768` and `-1`). After the framing fix, the full Noise project passes
38 cases. Writes now narrow with checked unsigned conversions, and the responder
reserves two additional bytes for its prefix.

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

## Parent session-key lifetime (#402, historical partial mitigation)

This section records the parent implementation and its Windows evidence. The
[current recovery contract and validation status](windows-process-key-recovery.md)
extend that implementation for newly marked keys after abnormal exit.

Before PR #416, the helper created a fresh persisted named Windows CNG key on
every call; both registered TLS roles and QUIC dial/listen use this helper. Only
lease disposal deleted it. That source trace confirmed the original crash-leak
premise; the audit's Windows GC and timing experiment was not independently
executed on this Linux host.

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
In PR #416, #402 remained partial and had no closing reference.

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

The Windows job keeps the full TLS/QUIC suites, requires 32 executed QUIC cases,
and retains the registered transport tests. The recorded Windows run confirms
the handshake, concurrency, certificate lifetime, and normal-process-exit cleanup
tests at the exact implementation revision. These are Windows execution results,
not inferred from Linux results. The CI gates remain intact.

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

These results resolve the previous Windows execution-evidence blocker. They do
not establish cross-client interoperability or hard-crash orphan recovery, and
they do not turn the #373 visibility probe into protocol enforcement.

## Validation commands

Linux checks used the repository root as the working directory and `TMPDIR` for
one shared artifact root. All builds used `-p:TreatWarningsAsErrors=true`:

```sh
dotnet test --project libp2p/Libp2p.Protocols.Noise.Tests/Libp2p.Protocols.Noise.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 38
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 29
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredIdentityTests' --minimum-expected-tests 45
dotnet test --project libp2p/Libp2p.Core.Tests/Libp2p.Core.Tests.csproj --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~CanonicalIdentityTests|FullyQualifiedName~IdentityTests|FullyQualifiedName~PeerIdTests' --minimum-expected-tests 1
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes
```

A first local QUIC invocation with `--minimum-expected-tests 32` discovered all 32
cases, passed 29, skipped the three Windows-only cases, and exited 9 because the
runner excludes skips from its execution minimum. The Linux command above uses
29; the Windows CI execution minimum remains 32. No CI gate was reduced.

The initial parent Noise baseline passed 20 tests. Retained Linux Release checks
at `84e0e2237eb5c8ce88ba59b747950f6a7cd2aa52`, using the commands above with
`-c Release`, passed Noise 38/38, TLS 32/32, QUIC 29 plus three Windows-only skips,
and registered E2E 45/45. The Core identity tests passed 75/75 during the initial
implementation. These results apply to unchanged protocol and test inputs;
documentation-only edits do not change those inputs. The recorded Windows run
supplies the native-platform results that Linux could not establish. All existing
independent review, final verification, and CI gates remain.

## Parent PR handoff (historical)

The only closing reference is:

```text
Closes #371
```

#373 remains unresolved because the Windows callback loses transmitted duplicate
entries. #402 is a partial mitigation of per-attempt key creation and retains one
possible orphan per crashed process run. Neither issue had a closing reference in
the parent. The current #402 follow-up uses the closing reference and dependency
in the [recovery record](windows-process-key-recovery.md).
The stacked dependency on PR #411 and target branch stated above must remain in
the PR description. The host owns publication and issue closure.
