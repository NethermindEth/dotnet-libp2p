# TLS Protocol

This implementation provides full support for the libp2p TLS handshake protocol as specified in the [libp2p TLS specification](https://github.com/libp2p/specs/blob/master/tls/tls.md).

## Features

### Protocol Compliance
- **TLS 1.3 Enforcement**: Only TLS 1.3 (and higher) is supported per libp2p spec
- **Protocol ID**: `/tls/1.0.0` as defined in the specification
- **ALPN**: Uses "libp2p" application protocol for negotiation
- **Peer Authentication**: Mutual TLS with libp2p public key extension

### Certificate Features
- **libp2p Extension**: Custom X.509 extension (OID: `1.3.6.1.4.1.53594.1.1`) for peer identity
- **Self-Signed Certificates**: Uses ephemeral keys for certificate signing, not host keys
- **Key Type Support**: Ed25519, Secp256k1, ECDSA, and RSA key types
- **Windows Compatibility**: Special handling for Windows CNG key requirements

### Security Features
- **Certificate Validation**: Comprehensive validation per libp2p spec:
  - Certificate validity period checking
  - Self-signed certificate requirement
  - Single certificate (no chains allowed)
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
├── Public Key: Ephemeral ECDSA key
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
- **Cross-platform**: Works on Linux, macOS, and Windows
- **Windows CNG**: Special handling for Windows named CNG keys for SslStream compatibility
- **Key Generation**: Creates persistent named keys on Windows, ephemeral keys elsewhere

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

