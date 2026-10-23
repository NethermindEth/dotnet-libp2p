# TLS certificate-list cardinality: baseline and prerequisites

<!-- cspell:words pathlib runsettings -->

Status: **implementation incomplete**. The production source remains at
`f94d0d071518dd43085303f6f24b5de7b2660e82`. This record captures the #373
reproduction and the compatibility gate encountered before switching the backend.
The [parent validation record](secure-transport-validation.md) remains historical
evidence for the parent fixes; its six visibility probes are unchanged.

## Contract and production reproduction

The [pinned libp2p TLS specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/tls/tls.md#peer-authentication)
requires a single received certificate and validation of its self-signature,
identity extension, validity and expected peer identity. The
[Go validator at the pinned reference revision](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto.go#L118-L161)
parses the raw received entries and requires exactly one. The specification and
[Go reference tests](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto_test.go)
were retrieved and inspected at those revisions in this session. Go tests and
Go/.NET interoperability were not executed.

`TlsCertificateListTests.RegisteredTlsRequiresExactlyOneReceivedCertificate`
uses `AddLibp2p`, the default builder's encryption ordering, TCP, production
multistream negotiation, and the registered `TlsProtocol`. A forwarding observer
records the production context and upgrades. The raw server rejects Noise so
outbound connections exercise fallback to TLS. The sender captures the final
Certificate serialization, including each DER entry; it does not infer the list
from a built trust chain. Leaf self-signature and expected identity remain valid.

Linux results on unchanged production source:

| Received list | Production client | Production server |
| --- | --- | --- |
| One valid certificate | Upgrades; control passes | Upgrades; control passes |
| Valid leaf + unrelated valid certificate | Upgrades; regression fails | Upgrades; regression fails |
| Valid leaf + duplicate leaf | Upgrades; regression fails | Upgrades; regression fails |
| Empty list | Authentication failure; control passes | Authentication failure; control passes |

Thus the regression command exits **2**, with four passing controls and four
intentional red results for #373. Negative cases require receiver-side
`AuthenticationException`, unchanged identity/address state, no TLS upgrade and
no session publication. Positive cases verify identity and reach the registered
post-TLS muxer selector. They do not complete Yamux or an application request;
the separate parent registered controls exercise those layers in both directions.

## Native size compatibility gate

`NativeCertificateSizeTests` sends one exact-size, valid libp2p certificate with
an unknown noncritical padding extension and `libp2p` ALPN. It checks the actual
serialized Certificate body, validates the leaf before each attempt, requires
mutual authentication and application delivery for acceptance, brackets the
boundary between 1 KiB and 2 MiB, and records adjacent-point repeat observations.
Cancellation/timeouts are failures, not size-rejection observations.

Recorded Linux environment: Ubuntu 22.04, x64, kernel 5.15.0.86, SDK 10.0.402,
.NET runtime 10.0.13, OpenSSL 3.0.2 (distribution build dated 2026-09-17),
Bouncy Castle 2.6.2 sender. The final TLS run passed 36/36 with no skips.

| Native role | AllowTlsResume | Sample accepted DER | Sample rejected DER | Repeat observations |
| --- | --- | ---: | ---: | --- |
| Client | false | 102391 | 102392 | accepted / rejected |
| Client | true | 102391 | 102392 | accepted / rejected |
| Server | false | 102391 | 102392 | accepted / rejected |
| Server | true | 65114 | 65115 | accepted / rejected in final run; varies across runs |

A one-entry TLS 1.3 Certificate body adds nine bytes to the DER; its handshake
header adds four more. With resumption disabled, both roles therefore accept a
102400-byte Certificate body and reject 102401. The rejection reports OpenSSL's
`excessive message size` before the managed validation callback.

The resumption-enabled server instead reports OpenSSL `internal error` near
65 KiB. An earlier run accepted DER size 65115 during the search but rejected
that same size on immediate retest. Disabling resumption removes this lower
ceiling. This agrees with the
[OpenSSL 3.0.2 ticket constructor](https://github.com/openssl/openssl/blob/openssl-3.0.2/ssl/statem/statem_srvr.c#L3654-L3682),
which rejects an encoded session larger than `0xFF00`; the
[session encoder](https://github.com/openssl/openssl/blob/openssl-3.0.2/ssl/ssl_asn1.c)
includes the peer certificate. Attribution of the observed failure to ticket
serialization is an inference supported by that source and the resumption
toggle, not a native stack trace. The installed distribution has backports;
the upstream tag is supporting source rather than its exact patched build.

The approved plan already disables resumption but also requires preserving
baseline acceptance limits and stopping for an amendment if they change.
**Before switching production, approval is needed to preserve the native ingress
cap while removing the incidental session-ticket ceiling.** Windows native
baseline evidence is also pending. No production backend or limits have changed.

The proposed managed backend remains Bouncy Castle 2.6.2, using its
[supported certificate parser limit](https://github.com/bcgit/bc-csharp/blob/release-2.6.2/crypto/src/tls/Certificate.cs)
and protocol interface for asynchronous I/O. Its default handshake-message bound is
32768 bytes, which would reject valid native controls. An explicit bound must
follow the approved policy and the Windows measurements.

## QUIC boundary

QUIC remains on MsQuic. Its TLS handshake is part of QUIC and does not pass
through negotiated `/tls/1.0.0`. The
[.NET 10.0.13 QUIC event handler](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicConnection.cs#L745-L758)
passes native certificate and chain pointers to certificate validation. The
public callback exposes certificate/chain objects rather than an ordered raw
TLS Certificate list. Neither the Linux `SslStream.ExtraStore` observations nor
the planned TLS adapter establish QUIC duplicate-entry visibility. QUIC
cardinality remains a separate representation limitation; its existing identity
and Windows key-lifetime checks must remain.

## Replay and retained evidence

The [portable Linux snapshot](evidence/linux-tls-cardinality-baseline.json)
records report hashes, test-input hashes, exact case outcomes, native boundary
observations and retrieved source hashes. Commands are recorded below. Raw NUnit XML was collected
in the session temporary directory; this snapshot does not claim a published CI
run or final-candidate validation.

From the repository root, with the session's existing `TMPDIR`:

```sh
python3 - <<'PY'
import os
from pathlib import Path
p = Path(os.environ['TMPDIR'])
(p / 'tls-cardinality.runsettings').write_text('<RunSettings><NUnit><TestOutputXml>' + str(p / 'tls-cardinality-results') + '</TestOutputXml></NUnit></RunSettings>')
PY
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --minimum-expected-tests 36 --timeout 180s --settings "$TMPDIR/tls-cardinality.runsettings"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~TlsCertificateListTests' --minimum-expected-tests 8 --timeout 120s --settings "$TMPDIR/tls-cardinality.runsettings"
dotnet format whitespace /proc/self/cwd/libp2p --folder --verify-no-changes
```

The second test command is expected to fail on the parent for the four extra-list
cases. Do not change its assertions or minimum to make the baseline green.

The parent registered controls passed 5/5, including Noise, TLS and QUIC
bidirectional application exchange, failed-dial retry, and both expected-peer
alias controls. The exact command was:

```sh
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release --artifacts-path "$TMPDIR/artifacts" -p:TreatWarningsAsErrors=true -- --filter 'FullyQualifiedName~RegisteredSecureTransportTests|FullyQualifiedName~RegisteredSecurityRejectsAnAliasExpectedPeerId' --minimum-expected-tests 5 --timeout 180s --settings "$TMPDIR/tls-cardinality-parent.runsettings"
```

Its settings used the same XML configuration, with output directed to
`$TMPDIR/tls-cardinality-parent-results` to retain separate reports.

For the required Windows baseline, use the latest test files and the host's
existing `TEMP`. Retain both XML files and `dotnet --info` output:

```powershell
"<RunSettings><NUnit><TestOutputXml>$env:TEMP/tls373-results</TestOutputXml></NUnit></RunSettings>" | Set-Content "$env:TEMP/tls373.runsettings"
dotnet --info
dotnet test --project libp2p/Libp2p.Protocols.Tls.Tests/Libp2p.Protocols.Tls.Tests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~NativeCertificateSizeTests" --minimum-expected-tests 4 --timeout 180s --settings "$env:TEMP/tls373.runsettings"
dotnet test --project libp2p/Libp2p.E2eTests/Libp2p.E2eTests.csproj -c Release -p:TreatWarningsAsErrors=true -- --filter "FullyQualifiedName~TlsCertificateListTests" --minimum-expected-tests 8 --timeout 120s --settings "$env:TEMP/tls373.runsettings"
```

The host returns that evidence to this implementation session. After the gate,
the remaining approved backend, lifecycle, interoperability, both-platform
candidate validation and host-managed independent review work is still required.
Existing tests and CI gates have not been reduced.

## PR handoff text to retain

Stacked on PR #416, targeting `feature/20261023-212739-3e2248`. Preserves the
parent fixes and is limited to TLS certificate-list cardinality enforcement.

Closes #373

This is required description text for the completed change, not a claim that
the issue is already fixed. The host owns commits and PR operations.
