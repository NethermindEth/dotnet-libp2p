# I2P transport

The I2P transport connects libp2p peers through an I2P router using the SAMv3 API.

The libp2p transport stack uses SAM `STREAM` sessions as a reliable byte stream:

```text
garlic32/garlic64 -> multistream -> noise or tls -> multistream -> yamux -> applications
```

Enable it with `WithI2p()`. The default SAM endpoint is `127.0.0.1:7656`.
Use `WithI2p(samHost, samPort, destinationKeyFile)` when the router endpoint or
persistent destination key path is not the default.

`WithI2p()` uses a plain SAM `STYLE=STREAM` session by default, so stream-only
transport remains compatible with SAM 3.1 routers. Direct `I2pSamClient` use
defaults to a shared SAM `MASTER` session, with `STYLE=STREAM` and
`STYLE=DATAGRAM` subsessions on the same I2P destination. Set
`I2pOptions.UsePrimarySessionForStreams` explicitly when a caller needs to
choose one behavior in a specific integration.

UDP-style I2P packets are available through
`I2pSamClient.CreateDatagramSessionAsync()`, which implements the SAM
`DATAGRAM` path. DATAGRAM sends require a full I2P base64 destination using the
I2P alphabet; b32 hostnames are not accepted at the UDP packet boundary. The
DATAGRAM path is intentionally not exposed as `ITransportProtocol` because the
current transport stack expects reliable ordered channels.
