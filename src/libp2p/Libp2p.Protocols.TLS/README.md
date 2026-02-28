# Libp2p TLS Protocol

This package provides TLS transport layer security for libp2p networks with Windows compatibility improvements.

## Features

- TLS 1.2 and 1.3 support with automatic fallback
- Windows-compatible certificate generation without Certificate Store dependencies
- ALPN (Application Layer Protocol Negotiation) support
- Cross-platform compatibility (Windows, Linux, macOS)
- libp2p specification compliant certificate extensions

## Usage

```csharp
var tlsProtocol = new TlsProtocol(loggerFactory);
// or for Windows-specific optimizations
var windowsTlsProtocol = new WindowsTlsProtocol();
```

## Windows Compatibility

This implementation includes Windows-specific workarounds for:
- Certificate Store access issues  
- .NET 9 SslStream callback handling
- ECDSA key generation compatibility
- Thread-safe stream operations