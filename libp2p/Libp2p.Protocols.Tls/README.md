# TLS Protocol

This implementation supports the libp2p TLS handshake protocol described in the [libp2p TLS specification](https://github.com/libp2p/specs/blob/master/tls/tls.md), with the received certificate-list limitation described below.

## Features

### Protocol Compliance
- **TLS 1.3 Enforcement**: Only TLS 1.3 (and higher) is supported per libp2p spec
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
- **Platform Validation**: Linux transport tests pass. Recorded validation of revision `84e0e2237eb5c8ce88ba59b747950f6a7cd2aa52` on Windows 10.0.26200 / .NET 10.0.12 passed QUIC 32/32, TLS 32/32, and registered transport/alias controls 5/5 without skips. The [validation record](../../docs/development/secure-transport-validation.md) links a portable results snapshot, replay commands, and the CI artifact configuration; all CI gates remain.
- **Windows Keys**: Schannel requires a named CNG key. Certificates share one process-owned key, with certificate creation serialized and deletion of that exact key attempted at normal process exit. Disposing a certificate does not delete the shared key; callers of `CertificateFromIdentity` should dispose the certificate and must not explicitly delete its private key.
- **Crash Lifetime**: Fresh Windows key initialization attempts recovery of exactly marked process keys after proving their owning process incarnation has terminated. Live owners, unknown liveness and unmarked legacy keys are retained. Contention, access restrictions and deletion failure defer recovery to another initialization. See the [recovery contract and validation status](../../docs/development/windows-process-key-recovery.md).

### Received certificate lists

The specification requires rejecting more than one transmitted certificate. The current validation verifies the leaf's self-signature and identity but does not enforce the transmitted list's cardinality. `SslStream` exposes a platform-built `X509Chain`, which can have one element even when the peer sent an unrelated certificate or a duplicate leaf. `ChainElements.Count == 1` therefore cannot enforce this requirement. On the tested Windows runtime, a duplicate leaf is also absent from `ExtraStore` in both roles: the callback exposes only the presented leaf, one chain element, and an empty `ExtraStore`, as in the valid single-certificate control. #373 remains unresolved. See the [probe and platform evidence](../../docs/development/secure-transport-validation.md).

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

