# TLS certificate-list cardinality (#373)

<!-- cspell:words pathlib runsettings backpressure Psks nonblocking GOCACHE GOMODCACHE hashlib hexdigest -->

The production `/tls/1.0.0` backend now uses Bouncy Castle 2.6.2 in both roles.
Recorded Windows and Linux certificate-list enforcement and lifecycle checks
passed. Native interoperability and pinned Go TLS/Yamux interoperability are
recorded below. The Windows 10 candidate passed all 126 checks without skips.
A subsequent Windows CI run timed out in two native interoperability controls.
After the retry-flight repair, Windows Server candidate `b295980` passed TLS
62/62, QUIC 32/32 and registered controls 36/36. Later shutdown changes have
separate Linux evidence: TLS 68/68 and registered paths 76/76. The PR feedback
repair below passes the expanded TLS suite 79/79 and registered paths 76/76 on
Linux; Windows and Go have not been rerun with these latest changes. Pinned Go
TLS/Yamux interoperability and reference tests also passed in the prior retained
continuation. **Host-managed independent re-review and final verification remain
required.** Windows observations apply only to their recorded source inputs.
The [parent validation record](secure-transport-validation.md) is unchanged.

## Contract, implementation and scope

The [pinned libp2p specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/tls/tls.md#peer-authentication)
requires a single received certificate, a valid self-signature, validity and
identity-extension verification, and the expected remote identity. The
[pinned Go validator](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto.go#L118-L161)
counts the raw received entries. The specification, Go validator and reference
vectors were inspected at the retained revisions. Go v0.41.1 resolves to the
required revision `8ce45df1bd142373cdf0a6f8355a757273258f49`; the module checksum
and origin are retained in candidate evidence.

Native integration was assessed in the approved plan. OpenSSL exposes a
[peer-sent chain with role-dependent leaf inclusion](https://docs.openssl.org/3.0/man3/SSL_get_peer_cert_chain/).
Schannel's [supported context query](https://learn.microsoft.com/en-us/windows/win32/secauthn/querycontextattributes--schannel)
returns a certificate context/store. No supported portable hook into the
existing public `SslStream` established exact duplicate-preserving cardinality.
The selected change replaces only the TLS protocol backend; it neither forks a
runtime nor uses reflection for enforcement.

The internal adapter uses the
[Bouncy Castle 2.6.2 nonblocking interface](https://github.com/bcgit/bc-csharp/blob/release-2.6.2/crypto/src/tls/TlsProtocol.cs).
One async owner serializes engine calls. I/O tasks do not access the engine;
one record of application data per direction can be pending. Reads can progress
while encrypted writes are pending. Header validation precedes record-body
allocation. Supported certificate-receive hooks validate nested bounds and exact
one-entry structure before certificate/list allocation; the
[backend parser](https://github.com/bcgit/bc-csharp/blob/release-2.6.2/crypto/src/tls/Certificate.cs)
and both authentication callbacks also enforce one entry. Duplicate DER entries
count separately. The existing leaf validator remains authoritative for identity
and self-signature checks.

Mutual TLS 1.3 is required; PSK, resumption and early application data are
unavailable. The client requires a certificate request. Validated identity stays
private until CertificateVerify and Finished verification and final handshake
output drainage. Existing expected PeerId, alias, relay, and prior-key conflict
checks remain. ALPN offers/selects `libp2p`, preserves absent-ALPN compatibility,
and rejects incompatibility. Post-TLS multistream/Yamux negotiation remains.

The constructor, protocol ID, methods, and public ALPN property types are
unchanged. Credentials use the actual certificate private key, including the
parent's process-owned Windows key, through the existing certificate lease.
Temporary PKCS#8 bytes are cleared. Channel writes receive stable owned arrays.
Cancellation reaches actual I/O; partial-write debt is aborted before owned work
is awaited and credentials are released. Clean close drains data and close alerts
before transport EOFs. Peer `close_notify` closes only its write half; the
application can still send its reply after reading EOF. Missing `close_notify` and truncated records fail. Upgrade
lifecycle exceptions keep their category after authentication. Before upgrade,
TLS I/O, cryptographic and identity failures become `AuthenticationException`;
implementation faults retain their original exception category.

Managed TLS changes cipher selection and reliance on OS crypto policy. See the
[TLS README](../../libp2p/Libp2p.Protocols.Tls/README.md) for that deployment change.

## Approved size policy

The user decision of 24 October 2026, **"373 - no limit"**, supersedes the former
native-size compatibility gate. The Windows/Linux native acceptance ceilings
below are historical observations, not application limits to preserve.

There is no additional application certificate-size cap. Bouncy Castle's
incidental 32768-byte handshake default is overridden with the actual TLS
Handshake body's uint24 maximum: **16777215 bytes**. With empty request context
and entry extensions, one DER certificate can occupy **16777206 bytes**. Record
and nested vector bounds remain. Reassembly can therefore consume a bounded TLS
message per connection, with additional certificate parsing copies; this is not
an aggregate memory budget across connections. No queues of unlimited records
are introduced.

Registered controls accept 131072-byte DER certificates above both old native
boundaries and exact 16777206-byte DER certificates at the protocol boundary, in
both roles. Large duplicate lists span records and reject. Tiny malformed
messages declaring oversized nested lists reject before proportional allocation.
All six original visibility probes and all four native size probes are unchanged.

## Historical red evidence

The [Linux snapshot](evidence/linux-tls-cardinality-baseline.json) and
[host-supplied Windows snapshot](evidence/windows-tls-cardinality-baseline.json)
retain the baseline on production revision
`f94d0d071518dd43085303f6f24b5de7b2660e82` with the original regression files.
The Windows run used Windows 10.0.26200, SDK 10.0.401/runtime 10.0.12. The host
provided raw NUnit XML, logs, commands, exit statuses and hash-matched test files;
the portable snapshot retains hashes and observations without private paths.

On **both real platforms**, both one-certificate controls and both empty-list
controls passed. All four unrelated/duplicate-extra cases failed intentionally:
the production receiver upgraded. Exit 2 is the red #373 reproduction, not a
passing test run. A sender's local TLS 1.3 handshake return is not used as proof
of receiver acceptance.

Windows native Certificate bodies accepted 32768 bytes and rejected 32769 in
both roles, regardless of resumption. Linux accepted 102400 and rejected 102401
with resumption disabled. Its resumption-enabled server also exhibited an
incidental, variable session-ticket ceiling near 65 KiB. These observations
motivated the explicit policy decision; they are not candidate limits.


The original eight parent red controls can also be replayed without moving HEAD.
This substitutes only the pinned parent TLS source and the original checkpoint's
wire tests; all other production source stays in the supplied checkout. Expect
exit **2**, four passing single/empty controls and four extra-entry failures:

```sh
python3 - <<'PYTHON'
import os, subprocess
from pathlib import Path
p = Path(os.environ['TMPDIR'])
for revision, source, target in [
    ('f94d0d071518dd43085303f6f24b5de7b2660e82', 'libp2p/Libp2p.Protocols.Tls/TlsProtocol.cs', 'ParentTlsProtocol.cs'),
    ('2515bb8df7976a1497baa3718b397c8aa3b9988c', 'libp2p/Libp2p.E2eTests/TlsCertificateListTests.cs', 'ParentCertificateListTests.cs'),
]:
    (p / target).write_bytes(subprocess.check_output(['git', 'show', revision + ':' + source]))
(p / 'tls373-red.targets').write_text(f"""<Project><Target Name="Tls373Red" BeforeTargets="CoreCompile">
<ItemGroup Condition="'$(MSBuildProjectName)' == 'Libp2p.Protocols.Tls'"><Compile Remove="TlsProtocol.cs;TlsHandshake.cs;TlsChannel.cs"/><Compile Include="{p / 'ParentTlsProtocol.cs'}"/></ItemGroup>
<ItemGroup Condition="'$(MSBuildProjectName)' == 'Libp2p.E2eTests'"><Compile Remove="TlsCertificateListTests.cs"/><Compile Include="{p / 'ParentCertificateListTests.cs'}"/></ItemGroup>
</Target></Project>""")
(p / 'tls373-red.xml').write_text('<RunSettings><NUnit><TestOutputXml>' + str(p / 'tls373-red-results') + '</TestOutputXml></NUnit></RunSettings>')
PYTHON
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts/parent" -p:TreatWarningsAsErrors=true -p:CustomBeforeMicrosoftCommonTargets="$TMPDIR/tls373-red.targets" -- --filter FullyQualifiedName~TlsCertificateListTests --minimum-expected-tests 8 --timeout 180s --settings "$TMPDIR/tls373-red.xml"
```

## Replay and retained evidence

The [Linux candidate snapshot](evidence/linux-tls-cardinality-candidate.json)
records source hashes, exact commands, discovered/executed counts, outcomes,
report hashes, interoperability and benchmark measurements. It was collected
after the pump corrections, before the host committed the tested source as
`19d9aa1d746d8e659d09cb82df4686772e41c1c0`. Its checkpoint and pending-Windows
status describe collection time; the completed Windows run below supersedes
that pending status. The snapshot remains unchanged as an execution record.

At documentation checkpoint `e7e84b07f9c387f2a9e6b0fd078de0b7b49bd350`, all
**466 Linux input hashes and 461 Windows input hashes** matched the files then.
That comparison also passed at `a03053656ab4634bd094ed5d0d68130aba3496ff`.
The native interoperability diagnostics changed `TlsChannelTests.cs`. The
subsequent retry repair changes `TlsChannel.cs`, adds `TlsRetryTests.cs`, and
raises both TLS CI minima to 62. The older complete TLS-suite results cannot
establish this repair's Windows outcome. Its [separate Linux record](evidence/linux-retry-tls-cardinality-candidate.json)
retains the repaired input hashes and regression results. The original Linux input digest is
`5c7410f4c31af8cdcee230fd1a6e9b59a72d6455aeac5c5eec1bd99636a181cb`.
Only reuse results whose relevant inputs match; repeat the comparison on a later
head:

```sh
python3 - <<'PY'
import hashlib, json
from pathlib import Path
for name in ['linux-tls-cardinality-candidate.json', 'windows-tls-cardinality-candidate.json']:
    record = json.loads((Path('docs/development/evidence') / name).read_text())
    for path, expected in record['inputs'].items():
        assert hashlib.sha256(Path(path).read_bytes()).hexdigest() == expected, path
    print(name, len(record['inputs']), 'input hashes match')
PY
```

Registered authentication tests use `AddLibp2p`, TCP and production multistream
negotiation, with an observer forwarding to the actual TLS implementation.
The default ordering falls back from rejected Noise to TLS. One-cert controls
reach the post-TLS muxer selector; separate parent registered controls complete
Yamux and application requests in both directions. Rejections require receiver
failure, unchanged identity/address state, no upgrade and no session publication.
No application channel is created for rejected peers. Extra cases cover invalid
self-signatures/extensions, validity, prior-key/expected-peer conflicts, missing
mutual authentication, invalid CertificateVerify, and omitted Finished.

Adapter coverage includes native `SslStream` interoperability in both roles,
ALPN presence/absence/mismatch, Finished followed by application data,
fragmented/coalesced records, large segmented simultaneous transfers, blocked
readers and writers, cancellation, disposal, truncated records, invalid record
lengths, nested allocation bounds and close ordering. Noise, QUIC and affected
Core lifecycle/channel tests remain. Linux QUIC's three Windows-only cases stay
excluded by their original conditions; the Windows minimum stays 32.

Final Linux results: TLS **58/58**, registered paths **76/76**, Noise **38/38**,
Core lifecycle/channel checks **123/123**, and QUIC **29 passed, 3 original
Windows-only skips**. All commands exited zero and use Release with warnings as
errors. Before the final completion-signal correction, five additional runs of
the read-wakeup fix passed all 40 registered identity cases (200 cases). Those
repetitions are labeled separately from the final-candidate suite.

From repository root, using the existing session `TMPDIR`:

```sh
python3 - <<'PY'
import os
from pathlib import Path
p = Path(os.environ['TMPDIR'])
(p / 'tls373.xml').write_text('<RunSettings><NUnit><TestOutputXml>' + str(p / 'tls373-results') + '</TestOutputXml></NUnit></RunSettings>')
PY
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 79 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~TlsCertificateListTests|FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId|FullyQualifiedName~RegisteredProtocolsUseCanonicalIdentitiesAndVerifySignatures' --minimum-expected-tests 76 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 29 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.Protocols.Noise.Tests/Libp2p.Protocols.Noise.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 38 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.Core.Tests/Libp2p.Core.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~PeerLifecycleTests|FullyQualifiedName~SessionCancellationTests|FullyQualifiedName~SessionLifetimeCompatibilityTests|FullyQualifiedName~ChannelTests' --minimum-expected-tests 123 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes
```

Earlier registered runs intermittently stalled during dialing or negotiation.
The continuation reproduced this in a diagnostic run: a read completed between
the pump's processing check and its wait-set construction. Filtering completed
reads out of that set lost the wakeup. The owner now includes all consumable
reads regardless of completion, excluding only reads held behind backpressure.
The new 1000-round bidirectional request/reply regression failed against the
retained pump and passes after this correction. Final suite and repetition
results are recorded in the candidate snapshot. The same review also corrected the channel-completion wait
signals: they stay in the wait set until the owner observes them, including a
racing completion.
No existing assertion, deadline or execution minimum was relaxed.

Go interoperability uses the checked-in [driver](../../tests/tls-interop/run.py)
and pinned module. It runs the existing transport-interop application, exercises
TLS/Yamux and authenticated identity, and completes Ping with each implementation
as dialer. It is separate from same-library tests. With Go 1.23+ and Redis:

```sh
GOCACHE="$TMPDIR/go-build" GOMODCACHE="$TMPDIR/go-mod" go -C tests/tls-interop build -o "$TMPDIR/tls373-go-peer" .
dotnet build samples/transport-interop/TransportInterop.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true
python3 tests/tls-interop/run.py --dotnet-dll "$TMPDIR/artifacts/bin/TransportInterop/release/TransportInterop.dll" --go-peer "$TMPDIR/tls373-go-peer" --output "$TMPDIR/tls373-interop"
```

The pinned Go `TestNewIdentityCertificates` and `TestVectors` reference tests
also run separately:

```sh
GOCACHE="$TMPDIR/go-build" GOMODCACHE="$TMPDIR/go-mod" go -C tests/tls-interop test github.com/libp2p/go-libp2p/p2p/security/tls -run 'TestNewIdentityCertificates|TestVectors' -count=1 -v
```

The driver owns and stops only its child services. The existing transport
benchmark's upload, download and stream-open-exchange scenarios compare the
parent TLS backend with the candidate; measurements and limitations are retained
in the snapshot. They are small loopback scenarios, not a full throughput study.

| Existing benchmark (three processes each) | Parent median | Candidate median |
| --- | ---: | ---: |
| Upload, 32 KiB | 0.3431 MiB/s | 0.3508 MiB/s |
| Download, 32 KiB | 0.2342 MiB/s | 0.2385 MiB/s |
| Stream open and exchange | 137.9538 ms | 130.5586 ms |

All 18 final processes completed. Prior measurements varied substantially; this
small loopback workload does not establish general throughput or cipher
performance. The interrupted candidate's earlier dial timeouts preceded the
reproduced wakeup correction. The parent backend was compiled from its exact
revision by replacing only the TLS compile inputs, without moving HEAD. Replay:

```sh
python3 - <<'PYTHON'
import os, subprocess
from pathlib import Path
p = Path(os.environ['TMPDIR'])
source = subprocess.check_output(['git', 'show', 'f94d0d071518dd43085303f6f24b5de7b2660e82:libp2p/Libp2p.Protocols.Tls/TlsProtocol.cs'])
(p / 'ParentTlsProtocol.cs').write_bytes(source)
(p / 'tls373-parent.targets').write_text('<Project><Target Name="Tls373ParentBackend" BeforeTargets="CoreCompile" Condition="\'$(MSBuildProjectName)\' == \'Libp2p.Protocols.Tls\'"><ItemGroup><Compile Remove="TlsProtocol.cs;TlsHandshake.cs;TlsChannel.cs"/><Compile Include="' + str(p / 'ParentTlsProtocol.cs') + '"/></ItemGroup></Target></Project>')
PYTHON
dotnet build libp2p/Libp2p.Transport.Benchmarks/Libp2p.Transport.Benchmarks.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true
dotnet build libp2p/Libp2p.Transport.Benchmarks/Libp2p.Transport.Benchmarks.csproj -c Release --artifacts-path "$TMPDIR/artifacts/parent" -p:TreatWarningsAsErrors=true -p:CustomBeforeMicrosoftCommonTargets="$TMPDIR/tls373-parent.targets"
for backend in candidate parent; do
  root="$TMPDIR/artifacts"
  if [ "$backend" = parent ]; then root="$root/parent"; fi
  for metric in upload download stream-open-exchange; do
    for sample in 0 1 2; do
      dotnet "$root/bin/Libp2p.Transport.Benchmarks/release/Libp2p.Transport.Benchmarks.dll" "$TMPDIR/benchmark-$backend-$metric-$sample.json" tcp-tls-yamux "$metric"
    done
  done
done
```

## Windows candidate validation and replay

The [host-supplied Windows result](evidence/windows-tls-cardinality-candidate.json)
on committed candidate `19d9aa1d746d8e659d09cb82df4686772e41c1c0` passed:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| TLS | 58 | 0 | 0 |
| QUIC | 32 | 0 | 0 |
| Registered transport and certificate-list cases | 36 | 0 | 0 |
| Total | 126 | 0 | 0 |

The host executed these on real Windows 10.0.26200 with SDK 10.0.401 and runtime
10.0.12, in Release with warnings as errors and unchanged test deadlines. All
three commands exited zero. The raw NUnit XML counts and SHA256 hashes were
checked against the committed portable record, along with the exit-code files
and runtime metadata. Both roles pass the one-certificate controls and reject
empty, unrelated-extra, and duplicate-extra lists. Controls above the native
size boundaries and at the TLS protocol bound also pass. The repeated mutual
authentication and key-lifetime TLS case that failed before the wakeup
correction now passes.

The supplied log from subsequent [Windows CI run 40107696514](https://github.com/NethermindEth/dotnet-libp2p/actions/runs/40107696514/job/120912701724)
reports failures in `NativeSslStreamInteroperatesInBothRoles(False,False)` and
`NativeSslStreamInteroperatesInBothRoles(False,True)`, each at the unchanged
15-second deadline. Both use the production TLS client against a native server.
The reported stack comes from awaiting the receiver during test cleanup, which
could replace the original exchange failure. The revised test preserves that
failure, reports the pending phase and task states, and observes its outstanding
I/O after closing the channels. A controlled local failure injection confirms
the original test masked the exchange error in all four roles/ALPN combinations;
the revised test retains it. This establishes the diagnostic correction, not the
cause of the Windows timeout. The four unchanged interoperability controls pass
on Linux before and after the diagnostic change. The Windows CI failure remains
unresolved for that checkpoint; the older passing Windows 10 record remains history.

The host then replayed exact diagnostic checkpoint
`56db959aaf8610a89c3db49043b6a107aabe27b0` in [Windows CI run 40109816912](https://github.com/NethermindEth/dotnet-libp2p/actions/runs/40109816912/job/120918996332).
On Windows Server 10.0.26100.0 / .NET 10.0.13, TLS passed 56/58: the same two
native-server cases timed out during the native handshake, with a blocked
`SslStream.ForceAuthenticationAsync` write ending when the deadline cancelled
the receiver. The host's Windows desktop 10.0.26200 / .NET 10.0.12 replay of the
same checkpoint passed TLS 58/58, QUIC 32/32 and registered controls 36/36.
Those platform observations are retained separately from the repair's results;
the local pass does not override the CI failure.

### Retry-flight deadlock repair

The new regression forces a TLS 1.3 HelloRetryRequest by limiting only the test
server to P-256. It confirms a retry occurred and writes the retry plus
compatibility ChangeCipherSpec in one channel write. The peer does not read the
client response until that complete write drains. The production client emits
its own ChangeCipherSpec and queues a second ClientHello. The old pump refused
to consume more input while that ClientHello remained queued, so each peer
waited for the other to read. The test observes the blocked client write and
unchanged authenticated state before requiring the peer flight to drain.

The pump now permits handshake input to progress while its response flight is
queued. Engine access remains serialized and encrypted writes remain ordered.
Record validation and handshake reassembly limits remain intact; after Finished,
the existing one-record application backpressure still applies. Group defaults,
certificate authentication and the 15-second native deadlines are unchanged.

The same four regression cases, including fragmented input and cancellation
with output blocked, all fail at the flight-drain wait when built with the exact
old pump. They pass with the repaired pump. The complete Linux TLS suite passes
62/62 and registered transport/identity cases pass 76/76, in Release with
warnings as errors. This proves the retry deadlock and local correction.
Fresh parent regressions also pass: Noise 38/38, Core lifecycle/channel 123/123,
and Linux QUIC 29 passed with its three original Windows-only exclusions.
The original CI log does not include a retry trace. The subsequent
[Windows Server retry-candidate run](evidence/windows-retry-b295980-tls-cardinality-candidate.json)
on exact revision `b2959809878b404ac5e855ec44c67ed0a4eeb042` passed TLS
62/62, QUIC 32/32 and registered controls 36/36, without skips, on Windows
10.0.26100 / .NET 10.0.13. All 552 source/build/configuration hashes were checked
against that revision's Git objects, along with the actual NUnit XML and full
job log. This establishes passing native controls after the retry repair; it
does not independently trace an HRR on the native Windows connection.
The later close-ordering repair changes the pump and has separate Linux proof.
No Windows result is transferred across that source change.

The earlier attempt to obtain Go 1.23.12 returned HTTP 404; that collection-time
limitation remains in its historical record. The continuation supplied a
verified Go 1.23.12 toolchain. The current transport application builds with
warnings as errors, and the checked-in driver passes authenticated TLS/Yamux
Ping in both roles against pinned Go revision
`8ce45df1bd142373cdf0a6f8355a757273258f49`. The pinned certificate identity and
reference-vector tests also pass. Exact commands and checksums are in the
[continuation evidence](evidence/linux-continuation-tls-cardinality-candidate.json).

Run the focused native and retry controls on the failing Windows runner before
the full suites below, and retain the failure phase, task states, runtime
metadata and NUnit XML:

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:TEMP/tls373-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:TEMP/tls373.xml"
dotnet --info
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~NativeSslStreamInteroperatesInBothRoles|FullyQualifiedName~TlsRetryTests" --minimum-expected-tests 8 --timeout 90s --settings "$env:TEMP/tls373.xml"
```

The [earlier failed checkpoint result](evidence/windows-065245c-tls-cardinality-candidate.json)
on `065245ca722dccc281d808df5dbaebe9b40c08f5` passed TLS 57/57 and QUIC 32/32,
without skips. Registered cases passed 35/36; the TLS repeated-authentication
lifetime case timed out, including in an isolated recheck (2/3 transport cases
passed). Thus all 31 cardinality/identity cases passed on real Windows, including
one-cert, unrelated/duplicate extras and protocol-size controls in both roles,
but **that checkpoint was not passing validation**. It predates the pump wakeup
correction and remains historical failure evidence. The later passing result
above establishes the revised pump's Windows behavior.

Replay the Windows suites with the commands below. Retain commit ID,
`dotnet --info`, full logs, exit codes and NUnit XML. Use the host's existing
`TEMP`; existing results remain reusable only while their relevant input hashes
match:

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:TEMP/tls373-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:TEMP/tls373.xml"
git rev-parse HEAD
dotnet --info
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 79 --timeout 180s --settings "$env:TEMP/tls373.xml"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 180s --settings "$env:TEMP/tls373.xml"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~TlsCertificateListTests|FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId" --minimum-expected-tests 36 --timeout 180s --settings "$env:TEMP/tls373.xml"
```

## Close ordering repair after independent review

Independent review of `b2959809878b404ac5e855ec44c67ed0a4eeb042` confirmed a
close-ordering race in the new adapter. A peer could finish transport teardown
while the local pump was still acknowledging its close alert or publishing
transport EOF. The error path then aborted an already published application EOF.
Another interleaving signalled transport completion after consuming the final
record body but before its read continuation reached the TLS owner.

The adapter now drains that read before deciding whether the peer sent
`close_notify`. An ended/aborted transport during the local close-alert write or
final transport EOF does not invalidate a received peer close. Clean cleanup
half-closes the channels, preserving published EOF. Application writes still
fail on transport errors; blocked plaintext delivery is aborted on transport
completion. Missing peer close alerts and truncated records remain failures.
Caller cancellation retains its category even when transport abort arrives
before linked cancellation callbacks run. This follows the independent read and
write closure contract in [RFC 8446 section 6.1](https://www.rfc-editor.org/rfc/rfc8446.html#section-6.1).

Four barrier-controlled cases force either peer to finish first during close
acknowledgement or transport EOF, including a consumed-but-unparsed peer alert.
A fifth controls the cancellation callback ordering. With the exact `b295980`
pump and identical test source, all five fail; the missing-alert negative control
passes. With the repair all six pass, and the complete TLS suite passes 68/68.
The [separate close-ordering evidence](evidence/linux-shutdown-tls-cardinality-candidate.json)
records commands, NUnit report hashes and final input hashes. All 60 parallel
repetitions of the 28-case adapter class passed (six workers, 1680 test cases),
and registered transport/identity cases passed 76/76. Earlier retry and Windows records remain unchanged historical evidence.
Both TLS CI minima increase to 68; existing deadlines and exclusions are unchanged.

Focused replay from repository root, with the existing session `TMPDIR`:

```sh
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~PeerTeardownAfterCloseNotifyPreservesEof|FullyQualifiedName~CallerCancellationWinsOverConcurrentTransportAbort|FullyQualifiedName~TransportAbortWithoutPeerCloseNotifyIsNotCleanEof' --minimum-expected-tests 6 --timeout 90s --settings "$TMPDIR/tls373.xml"
```

The latest Windows Server result covers the earlier retry repair at `b295980`;
it does not validate the later shutdown source. The host continuation does not
require another Windows execution for this revision. The replay commands above
now retain TLS 79, QUIC 32 and registered 36 minima. Independent re-review and final
verification must still cover the final source.

## Successful-handshake fixture teardown (F2)

The next independent review confirmed the close-ordering fix at `f2f83f3`, but
found a race in `Test_ConnectionEstablished_AfterHandshake` under full-suite
load. Its `Task.WhenAll` cleanup invokes channel closures in argument order.
Closing the first upgraded channel can abort the transport before the second
upgraded channel closes, so that peer correctly reports a missing TLS close
alert. Core `CloseAsync` is forced teardown, rather than the graceful EOF this
successful-connection test required. Existing adapter tests separately assert
that forced disposal fails pending writes and rejects a missing close alert.

A controlled fixture probe holds the listener open until the dialer's forced
close finishes; all three identity/address cases fail during teardown with the
reported I/O error. The corrected fixture instead sends and reads EOF in both
directions and strictly awaits both TLS tasks before final cleanup. Its payload,
canonical identity, relay-address and task-success assertions remain; no deadline
was extended. Cleanup observes abort failures only after a failure in the tested
path, preserving that original exception. Production code is byte-identical to
`f2f83f389fd9198907ba3318aad5fbef1cc6a539`.

The complete TLS suite passes 68/68. Forty full-suite executions in five parallel
workers pass all 2720 cases, including all 120 successful-handshake cases. Fresh
registered controls pass 76/76. The [continuation record](evidence/linux-continuation-tls-cardinality-candidate.json)
retains the fixture probe, loaded runs, current Go interoperability, reused
unchanged-input checks, and final source hashes. Both active reviewers must
recheck the fixture/evidence changes before final verification.

## PR feedback: close before final handshake write acknowledgement

Review of `7f27c7d64f1fa6c5ab9faaa082efe7dbf16b70d7` identified another
reachable ordering: a client has verified the server Finished, but its final
handshake write has not yet been acknowledged by the transport. The peer can
already have consumed and verified that write, upgrade, and send `close_notify`.
The client then tried to publish application EOF before an upgraded channel
existed. The adapter now defers that EOF until its existing final-output drain
gate upgrades. Identity publication and the drain gate are unchanged.

Two barrier-controlled tests hold the final write acknowledgement until the
client has parsed the peer close alert. They assert no early upgrade, release
the acknowledgement, observe clean EOF, and send a reply after EOF. Both whole
and fragmented reads are covered. Two additional tests inject an implementation
fault before upgrade in either role and require the original exception instance.
On the unchanged production source all four fail: two null dereferences and two
incorrect `AuthenticationException` translations. With the repair all four
pass. Authentication rejection and prior-identity conflict controls still pass.

Seven direct guard tests isolate the redundant cardinality checks. Test-only
reflection calls the private authentication callback and reads the actual peers
installed by production `Start()`; production enforcement uses no reflection.
The encoding tests use valid nested lengths so that only exact single-entry
consumption rejects a second entry. Backend tests parse duplicate DER using
each production peer's chain limit, independently of the other two guards.
Each mutation was built from a temporary source copy, leaving production source
unchanged during the experiment:

| Single disabled guard | Failing new cases | Remaining new guard cases |
| --- | --- | --- |
| Callback count changed from `!= 1` to `< 1` | 1 | 6 passed |
| Exact entry consumption check removed | 1 | 6 passed |
| Both backend chain limits changed from 1 to 2 | 2 | 5 passed |

On Linux, SDK 10.0.402 / runtime 10.0.13, the 11 focused cases pass, the full
TLS suite passes **79/79**, and the registered transport/identity selection
passes **76/76**, all without skips, in Release with warnings as errors. Both
CI discovery minima are now 79; deadlines and exclusions are unchanged. The
full-suite and registered replay commands above select these checks. The
focused selection is:

```sh
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~TlsCertificateGuardTests|FullyQualifiedName~TlsHandshakeClosureTests' --minimum-expected-tests 11 --timeout 90s --settings "$TMPDIR/tls373.xml"
```

Historical evidence JSON and the pinned interoperability driver remain
provenance and manual replay tools; their presence is not a CI execution claim.
Neither Windows nor pinned Go interoperability was rerun for this repair.
Earlier platform results retain their recorded source scope. The host-owned PR
description should report the latest Linux counts above and label older Windows
counts with their tested revision, rather than claim 58/58 on the revised source.
ALPN compatibility and the approved managed-backend policy remain unchanged.
Independent re-review and final verification must cover these changes.

## QUIC boundary and handoff

QUIC remains on MsQuic. The
[.NET 10.0.13 QUIC certificate event](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicConnection.cs#L745-L758)
passes native certificate/chain pointers to validation; the public callback does
not provide an ordered raw TLS Certificate list. Neither the native `SslStream`
observations nor this adapter prove QUIC duplicate-entry visibility. QUIC
cardinality remains a separate representation limitation, and its parent
identity and key-lifetime regressions remain intact.

The production-enforcement finding is addressed in code and demonstrated on
Windows and Linux. Host-supplied independent review of `19d9aa1` reported no
code defects, passed TLS 58/58 and registered paths 76/76, and ran the full E2E
suite with 277 passed and two pre-existing skips. Its fault injection removed
all three cardinality checks and made six extra-entry cases fail; the reviewer
then restored the source. These are reviewer-reported checks, separate from
the implementation's Linux and host Windows execution records above.

That review applies to `19d9aa1`; it does not approve a later head. The host must
complete the normal same-session independent re-review and final verification
on the final head, including these documentation changes, before publication.
Platform result reuse remains conditional on matching source/build/CI inputs.

Required PR description text:

> Stacked on PR #416, targeting `feature/20261023-212739-3e2248`. Preserves the parent fixes and is limited to TLS certificate-list cardinality enforcement.

Closes #373
