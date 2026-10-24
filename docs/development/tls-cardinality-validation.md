# TLS certificate-list cardinality (#373)

<!-- cspell:words pathlib runsettings backpressure Psks nonblocking GOCACHE GOMODCACHE -->

The production `/tls/1.0.0` backend now uses Bouncy Castle 2.6.2 in both roles.
Linux enforcement, lifecycle, native interoperability and pinned Go TLS/Yamux
interoperability have been executed. **Fresh Windows candidate execution and
host-managed independent review are still required before final verification.**
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
before paired EOFs. Missing `close_notify` and truncated records fail. Upgrade
lifecycle exceptions keep their category after authentication.

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

## Candidate validation and replay

The [Linux candidate snapshot](evidence/linux-tls-cardinality-candidate.json)
records source hashes, exact commands, discovered/executed counts, outcomes,
report hashes, interoperability and benchmark measurements. The candidate is
uncommitted work on checkpoint `2515bb8df7976a1497baa3718b397c8aa3b9988c`;
the host owns the eventual commit. Windows results below must apply to that
committed candidate, not the earlier native baseline.

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

From repository root, using the existing session `TMPDIR`:

```sh
python3 - <<'PY'
import os
from pathlib import Path
p = Path(os.environ['TMPDIR'])
(p / 'tls373.xml').write_text('<RunSettings><NUnit><TestOutputXml>' + str(p / 'tls373-results') + '</TestOutputXml></NUnit></RunSettings>')
PY
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 57 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~TlsCertificateListTests|FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId|FullyQualifiedName~RegisteredProtocolsUseCanonicalIdentitiesAndVerifySignatures' --minimum-expected-tests 76 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 29 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.Protocols.Noise.Tests/Libp2p.Protocols.Noise.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 38 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet test --project libp2p/Libp2p.Core.Tests/Libp2p.Core.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~PeerLifecycleTests|FullyQualifiedName~SessionCancellationTests|FullyQualifiedName~SessionLifetimeCompatibilityTests|FullyQualifiedName~ChannelTests' --minimum-expected-tests 1 --timeout 180s --settings "$TMPDIR/tls373.xml"
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes
```

One earlier 72-case registered run hit the existing 15-second RSA-case deadline
while other compilation was active. That unchanged case passed its isolated
retry; the final suite result and any remaining limitation are recorded in the
candidate snapshot. No assertion, deadline or execution minimum was relaxed.

Go interoperability uses the checked-in [driver](../../tests/tls-interop/run.py)
and pinned module. It runs the existing transport-interop application, exercises
TLS/Yamux and authenticated identity, and completes Ping with each implementation
as dialer. It is separate from same-library tests. With Go 1.23+ and Redis:

```sh
GOCACHE="$TMPDIR/go-build" GOMODCACHE="$TMPDIR/go-mod" go -C tests/tls-interop build -o "$TMPDIR/tls373-go-peer" .
dotnet build samples/transport-interop/TransportInterop.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true
python3 tests/tls-interop/run.py --dotnet-dll "$TMPDIR/artifacts/bin/TransportInterop/release/TransportInterop.dll" --go-peer "$TMPDIR/tls373-go-peer" --output "$TMPDIR/tls373-interop"
```

The driver owns and stops only its child services. The existing transport
benchmark's upload, download and stream-open-exchange scenarios compare the
parent TLS backend with the candidate; measurements and limitations are retained
in the snapshot. They are small loopback scenarios, not a full throughput study.

## Required Windows candidate run

Run after the host commits the candidate; retain commit ID, `dotnet --info`, full
logs, exit codes and NUnit XML. Use the host's existing `TEMP`:

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:TEMP/tls373-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:TEMP/tls373.xml"
git rev-parse HEAD
dotnet --info
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 57 --timeout 180s --settings "$env:TEMP/tls373.xml"
dotnet test --project libp2p/Libp2p.Protocols.Quic.Tests/Libp2p.Protocols.Quic.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 32 --timeout 180s --settings "$env:TEMP/tls373.xml"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~TlsCertificateListTests|FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId" --minimum-expected-tests 36 --timeout 180s --settings "$env:TEMP/tls373.xml"
```

## QUIC boundary and handoff

QUIC remains on MsQuic. The
[.NET 10.0.13 QUIC certificate event](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicConnection.cs#L745-L758)
passes native certificate/chain pointers to validation; the public callback does
not provide an ordered raw TLS Certificate list. Neither the native `SslStream`
observations nor this adapter prove QUIC duplicate-entry visibility. QUIC
cardinality remains a separate representation limitation, and its parent
identity and key-lifetime regressions remain intact.

The production-enforcement finding is addressed in code and Linux tests. Fresh
Windows candidate evidence and host independent review remain pending. Do not
publish the historical baseline as final Windows proof.

Required PR description text:

> Stacked on PR #416, targeting `feature/20261023-212739-3e2248`. Preserves the parent fixes and is limited to TLS certificate-list cardinality enforcement.

Closes #373
