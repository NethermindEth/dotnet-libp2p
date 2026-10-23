# Working with sessions

A session represents an established connection to a remote peer. It is created by dialing a peer address, an address set, or a peer id known to the `PeerStore`.

```csharp
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

ServiceProvider provider = new ServiceCollection()
    .AddLibp2p(builder => builder.AddAppLayerProtocol<ChatProtocol>())
    .BuildServiceProvider();

IPeerFactory peerFactory = provider.GetRequiredService<IPeerFactory>();
await using ILocalPeer peer = peerFactory.Create();

await peer.StartListenAsync();

Multiaddress remoteAddress = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001/p2p/12D3KooW...");
ISession session = await peer.DialAsync(remoteAddress);
```

`ILocalPeer.DialAsync` establishes the lower stack first: transport connection, protocol negotiation, encryption or authentication, and stream multiplexing. Once the session exists, application protocols can be opened over it.

## Dialing application protocols

Use `DialAsync<TProtocol>()` for protocols that do not need a request object:

```csharp
await session.DialAsync<PingProtocol>();
```

Use the typed overload when the protocol accepts a request and returns a response:

```csharp
int answer = await session.DialAsync<DeepThoughtProtocol, string, int>(
    "what is answer to the Ultimate Question?");
```

The remote side must expose a protocol with the same `ISessionProtocol.Id`. It does not have to use the same helper class locally; for example, one side can use a protocol implemented with `SymmetricProtocol` while the other uses a direct `ISessionProtocol` implementation, as long as the negotiated protocol id and wire format match.

## Dialing known peers

If discovery has populated the peer store, you can dial by peer id instead of carrying addresses through your code:

```csharp
using Nethermind.Libp2p.Core.Discovery;

PeerStore peerStore = provider.GetRequiredService<PeerStore>();
peerStore.Discover([remoteAddress]);

PeerId remotePeerId = remoteAddress.GetPeerId()!;
ISession knownPeerSession = await peer.DialAsync(remotePeerId);
```

The address must include a `/p2p/<peer-id>` component so the peer store can associate it with the peer id.

## Incoming sessions

Subscribe to `OnConnected` to react when a remote peer establishes a session with the local peer:

```csharp
peer.OnConnected += async remoteSession =>
{
    Console.WriteLine($"Connected to {remoteSession.RemoteAddress}");
    await remoteSession.DialAsync<PingProtocol>();
};
```

The handler runs after the lower protocol stack has created the session. Application protocol negotiation still happens when you call `session.DialAsync<...>()`.

## Shutting down

Await `peer.DisposeAsync()` to stop its listeners and close its registered connections and sessions. Once disposal starts, transport connections finishing their handshake cannot register new sessions. New dial and listen calls fail with `ObjectDisposedException`. Disposal waits for listener teardown and removes their advertised addresses. Cancelling the token passed to `StartListenAsync` can also stop those listeners independently.

Concurrent disposal calls await the same cleanup, including a session already disconnecting. A failing session-close callback is reported after the other sessions have also been closed.

## Resource limits and deadlines

The default stack bounds inbound TCP connections to **256 per local peer**, including
connections still shutting down. At most **32** may come from one source IP address,
across all of that peer's TCP listeners. Source ports do not create new allowances;
IPv4-mapped IPv6 addresses count as IPv4. Excess sockets close immediately before
protocol dispatch or receive-buffer rental. Separate local peers have separate TCP
allowances. Deployments with many peers behind one NAT may need a larger per-IP limit.

An inbound connection has **15 seconds** from connection-context creation to session
registration, including security negotiation and the wait for the first Yamux stream.
Both inbound and outbound sessions then have **15 seconds** to finish initialization,
including Identify negotiation and its response. Expiry disconnects the session,
cancels I/O, and releases its peer-store retention. Established sessions survive these
deadlines. Each multistream negotiation also has a **15 second** deadline which stops
before the selected protocol runs, so long-lived application streams remain supported.
Negotiation frames are limited to 1,024 payload bytes, including the trailing newline.

Configure immutable limits through DI before resolving the factory:

```csharp
ServiceProvider provider = new ServiceCollection()
    .AddLibp2p()
    .AddSingleton(new PeerResourceLimits(
        maxInboundTcpConnections: 512,
        maxInboundTcpConnectionsPerIp: 64,
        maxConcurrentInitializations: 128,
        inboundUpgradeTimeout: TimeSpan.FromSeconds(20),
        initializationTimeout: TimeSpan.FromSeconds(20),
        negotiationTimeout: TimeSpan.FromSeconds(15)))
    .BuildServiceProvider();
```

Direct `LocalPeer`, `PeerFactory`, `Libp2pPeer`, and `Libp2pPeerFactory` construction
also accepts `resourceLimits` and `timeProvider`. Positive finite deadlines are
required. Register a `TimeProvider` in DI to control time in tests.

At most **128 initialization operations** may run per local peer; excess sessions
are refused without queueing. This allowance remains charged until the initializer
returns, even after a timeout. Custom `ConnectedTo` overrides should pass
`session.Closed` into every asynchronous operation. An application operation which
ignores cancellation can keep an initialization allowance, but its expired session
cannot keep a peer-store slot pinned.

Identify, Identify Push, Ping, relay, and enabled pubsub protocols each allow **512
inbound streams total and 64 per remote peer**. All pubsub versions share one
allowance. Totals span local peers sharing a builder's protocol registrations. Replace
`Libp2pPeerFactoryBuilder.ProtocolLimits`, or register a `BuiltInProtocolLimits` instance
in DI, to change individual allowances. Setting a family's allowance to `null`
explicitly disables its cap. Application-added protocols remain uncapped unless
`AddProtocol(..., inboundLimit: ...)` supplies an allowance.

The TCP admission limits apply before context creation. Optional transports share
core initialization and negotiation protections after context creation; their native
handshakes and transport-native admission are outside these TCP limits.
