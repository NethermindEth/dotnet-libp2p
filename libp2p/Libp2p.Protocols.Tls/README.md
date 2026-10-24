# TLS Protocol

This implementation supports the libp2p TLS handshake protocol described in the [libp2p TLS specification](https://github.com/libp2p/specs/blob/master/tls/tls.md), using the Bouncy Castle 2.6.2 TLS backend.

## Features

### Protocol Compliance
- **TLS 1.3 Enforcement**: Only TLS 1.3 is enabled
- **Protocol ID**: `/tls/1.0.0` as defined in the specification
- **ALPN**: Uses "libp2p" application protocol for negotiation
- **Peer Authentication**: Mutual TLS with libp2p public key extension

### Certificate Features
- **libp2p Extension**: Custom X.509 extension (OID: `1.3.6.1.4.1.53594.1.1`) for peer identity
- **Self-Signed Certificates**: Uses session keys for certificate signing, separate from host identity keys
- **Key Type Support**: Ed25519, Secp256k1, ECDSA, and RSA key types
- **Session Keys**: Certificates use the supplied session key outside Windows and a shared process-owned P-256 key on Windows. Each certificate owns its private-key handle.

### Security Features
- **Certificate Validation**: Comprehensive validation per libp2p spec:
  - Certificate validity period checking
  - Self-signed certificate requirement
  - libp2p extension presence verification
  - Peer ID matching
  - Cryptographic signature verification
- **Client Authentication**: Required mutual authentication
- **Signature Verification**: Verifies signature over "libp2p-tls-handshake:" prefix + certificate public key

## Usage

The TLS protocol is automatically integrated into the libp2p connection stack. It handles:

1. **Certificate Generation**: Creates ephemeral certificates with libp2p identity embedded
2. **Handshake**: Performs TLS 1.3 handshake with ALPN "libp2p" protocol
3. **Peer Verification**: Validates remote peer certificates against expected peer ID
4. **Secure Channel**: Establishes encrypted communication channel

## Implementation Details

### Certificate Structure
```
X.509 Certificate:
├── Subject: SERIALNUMBER=<random-hex>
├── Issuer: Same as Subject (self-signed)
├── Public Key: Session ECDSA key
├── Extensions:
│   └── libp2p Public Key Extension (1.3.6.1.4.1.53594.1.1):
│       └── SignedKey (ASN.1):
│           ├── publicKey: libp2p peer identity public key (protobuf)
│           └── signature: Signature over "libp2p-tls-handshake:" + cert public key
└── Validity: 1 day before creation to far future
```

### Signature Process
1. Export certificate's public key as `SubjectPublicKeyInfo`
2. Concatenate "libp2p-tls-handshake:" prefix with the key info
3. Sign the result using the peer's private identity key
4. Embed signature in the libp2p extension

### Platform Support
- **Platform Validation**: Earlier candidates passed TLS 58/58, QUIC 32/32, and registered controls 36/36 on Windows desktop 10.0.26200. Windows Server CI subsequently timed out in two native-server interoperability controls. A forced HelloRetryRequest regression reproduces an input/output deadlock, and the repaired pump passes Linux TLS 62/62 and registered paths 76/76. Fresh Windows Server evidence is required to establish that this repair resolves those failures. The [#373 validation record](../../docs/development/tls-cardinality-validation.md) retains the earlier passing and failing observations, current results and replay commands. Final independent re-review and verification remain required for the repaired head.
- **Parent Validation**: The earlier native backend at revision `84e0e2237eb5c8ce88ba59b747950f6a7cd2aa52` passed QUIC 32/32, TLS 32/32, and registered transport/alias controls 5/5 on Windows without skips. The historical [parent validation record](../../docs/development/secure-transport-validation.md), replay commands, and CI gates are retained.
- **Windows Keys**: The shared certificate helper retains the named CNG key needed by native QUIC. TLS uses the certificate's actual key. Certificates share one process-owned key, with certificate creation serialized and deletion of that exact key attempted at normal process exit. Disposing a certificate does not delete the shared key; callers of `CertificateFromIdentity` should dispose the certificate and must not explicitly delete its private key.
- **Crash Lifetime**: A crash or failed shutdown cleanup can leave one key per process run behind. Across repeated crashes this residual is unbounded; no legacy-key sweep is performed. See the [ownership and validation evidence](../../docs/development/secure-transport-validation.md).

### Received certificate lists and backend

Negotiated `/tls/1.0.0` uses Bouncy Castle 2.6.2 on Windows and Linux. Both roles
require exactly one received Certificate entry, including when the extra entry
is a duplicate of the leaf. Structural checks run through supported TLS receive
hooks before certificate-list allocation; the backend parser and authentication
callbacks also enforce the single-entry rule. A built `X509Chain` is not used to
count transmitted entries.

The existing certificate helper validates the self-signature, validity, libp2p
identity extension and expected PeerId. Identity is published only after
CertificateVerify and Finished verification and after final handshake output has
drained. Client certificates are required. PSK, resumption and early application
data are disabled. `libp2p` ALPN is selected when offered; the existing absent
ALPN compatibility and post-TLS multistream muxer negotiation remain.

There is no additional application certificate-size cap, following the approved
24 October 2026 decision. TLS still bounds a handshake body to 16,777,215 bytes
(its 24-bit length field), and the Certificate context/list/entry/extension
lengths must fit that body. With empty context and entry extensions, a single
DER certificate can occupy at most 16,777,206 bytes. Record lengths are validated
by the backend before body allocation. Reassembly is bounded by the TLS message
limit; pending application data is bounded to one record per direction. Channel
writes receive immutable owned arrays, and connection shutdown observes its I/O
before disposing credentials.

The managed backend uses Bouncy Castle's TLS 1.3 cipher and cryptographic
implementation rather than Schannel/OpenSSL cipher configuration. Deployments
that relied on OS cipher or crypto-policy enforcement must account for this
backend change. The public protocol API and certificate lease/key ownership
contracts remain. QUIC stays on MsQuic and has a separate certificate callback
representation; this change does not establish QUIC list cardinality.

See the [#373 validation record](../../docs/development/tls-cardinality-validation.md)
for Windows/Linux enforcement results, historical failed checkpoints, native
and Go interoperability results, limits, replay commands, and the remaining
final-review and verification gates. The six original native visibility probes
and parent validation evidence are retained.

## Testing

The implementation includes comprehensive test coverage:
- Basic certificate generation and validation
- Network-level TLS handshake testing
- Bidirectional data exchange validation
- libp2p specification compliance tests
- Error condition handling

Run tests with:
```bash
dotnet test Libp2p.Protocols.Tls.Tests
```

## References
- [libp2p TLS Specification](https://github.com/libp2p/specs/blob/master/tls/tls.md)
- [RFC 8446 - TLS 1.3](https://tools.ietf.org/html/rfc8446)
- [libp2p Extension OID Registration](https://www.iana.org/assignments/enterprise-numbers/enterprise-numbers)

