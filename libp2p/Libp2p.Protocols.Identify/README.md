# Identify protocol

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/identify)

Stored protocol metadata keeps every matching locally installed protocol, using
the local stack's order and strings, then fills any remaining slots up to 64 with
other advertised IDs in their received order. IDs are distinct; unknown IDs are
at most 128 UTF-8 bytes. Locally configured IDs reuse their local string even when
longer. The entry count is bounded by the greater of 64 and the number of
distinct locally installed protocol IDs. Remote ordering therefore cannot hide
a supported protocol version behind unrelated IDs.

## Finding a candidate public IP

Choose a configured public IP when the operator knows it. Configuration works
before any peers connect and must be updated when the public IP changes.

On dynamic networks, enable public IP discovery and let ordinary Identify
exchanges collect reports. Identify's observed address describes the connection
source seen by the remote peer. Outbound connections can use ephemeral ports.
[Identify observed address](https://github.com/libp2p/specs/blob/78e75c6f4e5dd9de4bc1524c686c0f45df4b461a/identify/README.md#observedaddr)

```csharp
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

await using var builder = new Libp2pPeerFactoryBuilder().WithPublicAddressDiscovery();
await using ILocalPeer peer = builder.Build().Create();

// Connect to peers as usual before querying for a candidate.
IPAddress? candidate = await peer.GetPublicAddressDiscovery()!.GetCandidateAsync(
    AddressFamily.InterNetwork, cancellationToken: cancellationToken);
```

Discovery considers other authenticated peers with direct, literal public endpoints.
Noise, TLS, QUIC and WebRTC direct supply the authentication signal. Plaintext
only exchanges keys. Custom security and transport protocols can set
`State.IsRemoteIdentityAuthenticated` after authenticating the remote identity.
Reports come from successful ordinary Identify requests. The collector accepts
public IPv4 and IPv6 TCP or UDP endpoints and handles the common fixed-length
transport suffixes. The IP family and TCP or UDP transport must match the
reporter's direct connection. The collector discards ports and normalizes
IPv4-mapped IPv6 to IPv4 before comparing families.

Each local peer retains up to 32 reports per IP family for 10 minutes. A valid
report replaces any previous report from the same peer ID or reporter network
in that family. A reporter network is a single IPv4 address or an IPv6 /56.
Two or more independent reports must agree and form a strict majority. This
heuristic follows Go's approach to grouping observers.
[Go observed address manager](https://github.com/libp2p/go-libp2p/blob/8582b880e88ed4969b80d000f99adbc052be7fe1/p2p/host/observedaddrs/manager.go)

With insufficient agreement, `GetCandidateAsync` returns `null` or calls
an optional application-supplied lookup once. The lookup can work before peers
connect and reports the public IP of the network path it uses. The application
chooses the service and cancellation deadline. The result must be a public IP
of the requested family. Discovery uses the network only through that callback.

Pass bind addresses to `StartListenAsync`. Choose an advertised endpoint with a
separately established listener or forwarding port. Verify public reachability
by testing a remote dial back to that endpoint.
[AutoNAT v2](https://github.com/libp2p/specs/blob/78e75c6f4e5dd9de4bc1524c686c0f45df4b461a/autonat/autonat-v2.md)
