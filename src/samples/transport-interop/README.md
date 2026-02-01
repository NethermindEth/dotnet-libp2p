# Transport interop app

A .NET libp2p transport interoperability testing application that supports TCP and QUIC transports.

## Environment Variables

Required:
- `TRANSPORT`: Transport protocol to use (`tcp`, `quic-v1`)
- `IS_DIALER`: Whether this instance is a dialer (`true`) or listener (`false`)
- `TEST_KEY`: Key prefix for Redis communication
- `SECURE_CHANNEL`: Security protocol (`noise`) - not required for stackless protocols like `quic-v1`
- `MUXER`: Multiplexer protocol (`yamux`) - not required for stackless protocols like `quic-v1`

Optional:
- `REDIS_ADDR`: Redis server address (default: empty)
- `LISTENER_IP`: IP address to listen on (default: `0.0.0.0`)
- `TEST_TIMEOUT_SECS`: Test timeout in seconds (default: `180`)

## Build with docker

```sh
docker build -f ./src/samples/transport-interop/Dockerfile .
```
