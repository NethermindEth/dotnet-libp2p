# KadDHT Demo

A minimal Kademlia DHT demo over libp2p.

## Running

```bash
# Terminal 1 — start a listener
dotnet run --no-build -- --listen /ip4/127.0.0.1/tcp/40001

# Terminal 2 — connect to the listener (copy PeerID from Terminal 1 output)
dotnet run --no-build -- --listen /ip4/127.0.0.1/tcp/40002 \
    --bootstrap /ip4/127.0.0.1/tcp/40001/p2p/<PeerID>

# Terminal 3 — add a third node
dotnet run --no-build -- --listen /ip4/127.0.0.1/tcp/40003 \
    --bootstrap /ip4/127.0.0.1/tcp/40001/p2p/<PeerID>
```

### Local-only mode (no remote bootstrap nodes)

```bash
dotnet run --no-build -- --local-only --listen /ip4/127.0.0.1/tcp/40001
```

### Options

| Flag | Description |
|---|---|
| `--listen, -l <addr>` | Bind to a specific multiaddress |
| `--bootstrap, -b <addr>` | Connect to a bootstrap peer (repeatable) |
| `--no-remote-bootstrap` / `--local-only` | Skip default bootstrap nodes |
| `--help, -h` | Show help |

## Logs

Logs are written to **both** the console and a file called `kad-dht-demo.log` in the working directory. To follow the log file in real time:

```bash
# Linux / macOS
tail -f kad-dht-demo.log

# PowerShell
Get-Content kad-dht-demo.log -Wait
```