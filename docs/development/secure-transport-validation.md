# Secure transport behavior and limitations

## Noise handshake framing

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
  a remote identity or upgrading. These failures use the routine-failure logging
  classification.

Writes use checked unsigned conversions. The responder reserves two additional
bytes for its framing prefix. The incoming-frame regressions do not exercise
the responder's output-buffer capacity boundary.

Short encrypted transport records are a separate concern tracked in
[issue #431](https://github.com/NethermindEth/dotnet-libp2p/issues/431).

## Received TLS certificate lists

The [TLS peer-authentication specification](https://github.com/libp2p/specs/blob/c750236de43cd8b246a824b377dfdb26bae686db/tls/tls.md#peer-authentication)
requires rejecting multiple **transmitted** certificates, including duplicate
entries. The [Go validator](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto.go)
checks the length of the list parsed from the TLS callback's raw certificates.
See its [reference tests at the same revision](https://github.com/libp2p/go-libp2p/blob/8ce45df1bd142373cdf0a6f8355a757273258f49/p2p/security/tls/crypto_test.go)
for certificate-list rejection cases.

`CertificateListProbeTests` uses Bouncy Castle 2.6.2 as a TLS 1.3 sender in both
directions. It captures and parses the final Certificate serialization hook and
then invokes the original sender. The
[pinned sender implementation](https://github.com/bcgit/bc-csharp/blob/release-2.6.2/crypto/src/tls/TlsProtocol.cs)
encodes that same certificate directly into the handshake message, without chain
building or entry filtering. The probe compares exact DER entries with the
expected emitted list, checks the callback leaf, verifies the leaf self-signature
and expected libp2p identity, and exchanges an application byte. It does not infer
emission from `SslStreamCertificateContext` or a configured collection.

`SslStream` exposes a platform-built `X509Chain`, which can contain one element
even when the peer transmitted an unrelated certificate or a duplicate leaf.
`ChainElements.Count == 1` therefore cannot enforce the transmitted-list limit.
The current validation verifies the leaf's self-signature and identity but does
not enforce the transmitted list's cardinality. This limitation is tracked in
[issue #373](https://github.com/NethermindEth/dotnet-libp2p/issues/373).

The probes observed different `ExtraStore` behavior on Linux (.NET 10.0.13,
OpenSSL 3.0.2) and Windows (10.0.26200, .NET 10.0.12). Linux retained duplicate
entries, with leaf inclusion differing by role. On Windows, a duplicated leaf
produced one chain element and an empty `ExtraStore` in both roles, just like
the valid single-certificate control. These collections cannot expose the exact
transmitted list portably:

- [.NET 10.0.13 Unix extraction](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Security/src/System/Net/CertificateValidationPal.Unix.cs)
  copies the OpenSSL peer chain into `ExtraStore`.
- [Windows extraction](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Net.Security/src/System/Net/CertificateValidationPal.Windows.cs)
  obtains `ExtraStore` through a certificate store. The
  [store enumerator](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/Common/src/Interop/Windows/SChannel/UnmanagedCertificateContext.IntPtr.cs)
  excludes the leaf context and does not expose wire-list multiplicity.
- The [public ExtraStore contract](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chainpolicy.extrastore?view=net-10.0)
  describes chain-building inputs without guaranteeing exact wire entries.

Platform-conditioned assertions in `CertificateListProbeTests` pin the observed
extraction behavior. If a platform update changes the result, compare the OS and
runtime, serialized certificate list, callback leaf, and callback collections.
Reproduce both roles and all list shapes before updating expectations.

## Certificate lifetime

TLS disposes its native `SslStream` before the associated certificate lease on
both success and authentication failure. The disposal order is documented in
both role methods; existing TLS and E2E assertions do not pin that ordering.
See [Windows process-key lifetime and recovery](windows-process-key-recovery.md)
for shared-key ownership, normal-exit cleanup, and recovery after a crash.

`RegisteredSecureTransportTests` exercises registered Noise, TLS, and QUIC peers
through failed expected-peer handshakes, successful retries, mutual
authentication, collection, and application requests in both directions.
The [Test workflow](../../.github/workflows/test.yml) runs the transport suites
and Windows key-lifetime checks.
