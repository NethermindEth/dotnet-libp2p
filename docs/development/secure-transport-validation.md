# Secure transport validation

This work is stacked on PR #411, targeting `feature/20261023-122715-498974`,
with parent `0e731cc8b84ad51fd7af3bc31e58744678e0a427`. It preserves the
parent's identity canonicalization, certificate self-signatures, expected peer
checks, and registered protocol stack.

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

Windows observations are pending CI. No portable cardinality enforcement is
claimed, and no chain-count substitute was added. The unsupported full-conformance
claim was removed from the TLS README. Keep #373 open. The existing Windows TLS
job runs the six probes and uploads their NUnit XML observations.

## Session-key lifetime (#402, pending Windows CI)

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
assemblies each own one key. Do not describe #402 as crash-safe or fully resolved
without accepting this bound and obtaining Windows transport evidence.

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

The Windows job keeps the full TLS/QUIC suites, raises QUIC's discovery floor to
32 executed cases, and retains the registered transport tests. Windows handshake, concurrency,
and subprocess-cleanup results are still pending CI; none are claimed from Linux.

## Validation commands

Run from the repository root with the session's existing `TMPDIR`. All builds
used `-p:TreatWarningsAsErrors=true` and one artifact root:

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

The initial parent Noise baseline passed 20 tests. The revised Linux results are
Noise 38/38, TLS 32/32, QUIC 29 passed plus three Windows-only skips, registered E2E
45/45. The Core identity tests passed 75/75 during the initial implementation;
they were unchanged and were not rerun for this revision. Windows CI retains the
existing full protocol suites and registered lifetime/expected-peer checks. Its
results and renewed independent host review are required before accepting the
Windows compatibility correction. #373 remains unresolved, and #402 retains the
explicit crash-leak bound above. The host owns PR publication and issue closure;
closing references alone must not be presented as evidence of resolution.
