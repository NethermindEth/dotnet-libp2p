# Performance Benchmarks

A .NET libp2p performance testing application that measures upload/download throughput and latency between dialer and listener instances.

## Prerequisites

This sample requires Redis for coordination between dialer and listener instances.

**Start Redis before running the application:**

```bash
docker run -d --name redis-perf -p 6379:6379 redis:alpine
```

## Usage

### Run as Listener (waits for dialer connections)

```bash
# Set the listener IP address
$env:LISTENER_IP="127.0.0.1"
dotnet run
```

### Run as Dialer (connects to listener and runs tests)

```bash
# Configure as dialer
$env:IS_DIALER="true"
$env:LISTENER_IP="127.0.0.1"
dotnet run
```

## Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `IS_DIALER` | Set to `"true"` to run as dialer, `"false"` or unset for listener | `false` |
| `REDIS_ADDR` | Redis server address for coordination | `localhost:6379` |
| `LISTENER_IP` | IP address for the listener to bind to | Required |
| `TRANSPORT` | Transport protocol (`tcp`, `quic`, `quic-v1`) | `tcp` |
| `UPLOAD_BYTES` | Amount of data to upload per iteration | `1073741824` (1GB) |
| `DOWNLOAD_BYTES` | Amount of data to download per iteration | `1073741824` (1GB) |
| `UPLOAD_ITERATIONS` | Number of upload test iterations | `10` |
| `DOWNLOAD_ITERATIONS` | Number of download test iterations | `10` |
| `LATENCY_ITERATIONS` | Number of latency test iterations | `100` |

## Test Workflow

1. Start Redis: `docker run -d --name redis-perf -p 6379:6379 redis:alpine`
2. Start listener: `$env:REDIS_ADDR="localhost:6379"; $env:LISTENER_IP="127.0.0.1"; dotnet run`
3. Start dialer (in another terminal): `$env:IS_DIALER="true"; $env:REDIS_ADDR="localhost:6379"; $env:LISTENER_IP="127.0.0.1"; dotnet run`
4. The dialer will run performance tests and output results
5. Stop Redis when done: `docker stop redis-perf && docker rm redis-perf`

## Docker Build

```bash
# Build from repository root
docker build -f src/samples/perf-benchmarks/Dockerfile -t perf-benchmarks .
```