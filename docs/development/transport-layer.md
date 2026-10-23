# Developing transport and connection protocols

dotnet-libp2p has two protocol layers below application/session protocols:

- Transport protocols establish network connections from peer addresses, for example TCP or QUIC.
- Connection protocols run over an established channel and handle protocol negotiation, peer authentication, encryption, session creation, or stream multiplexing. Examples include multistream-select, noise, TLS, and yamux.

A protocol may cover several responsibilities. QUIC is the common example because the transport also includes security and multiplexed streams.

**Session** is an important concept in dotnet-libp2p. Client code can access an established session to inspect peer information, dial application protocols, and manage connections. The lower protocol stack exists to establish those sessions.

The usual routine of a protocol is to:

- listen for an inbound connection or dial a remote peer
- complete any required handshake
- start the upper layer protocol and forward communication to it

## Transport protocol

Transport protocols implement `ITransportProtocol`. Their goal is to turn a peer address into a live connection.

Implementations provide static helpers for address matching and default listener addresses:

```csharp
public interface ITransportProtocol : IProtocol
{
    static abstract Multiaddress[] GetDefaultAddresses(PeerId peerId);
    static abstract bool IsAddressMatch(Multiaddress addr);

    Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token);
    Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token);
}
```

When a real connection is established, the implementation should:

- create a connection context with `ITransportContext.CreateConnection()`
- fill `connection.State.LocalAddress` and `connection.State.RemoteAddress` when they are known
- call `connection.Upgrade()` to create the upper channel
- forward bytes between the upper channel and the remote peer

Listeners should also call `context.ListenerReady(realListenAddress)` after binding so libp2p can advertise the actual address, including any OS-assigned port.

When a listener closes its listening endpoints, it can call `context.ListenerStopped()` before draining accepted connections. This lets peer disposal finish even when a handler awaits disposal itself. The transport still owns its accepted connections and admission allowances until `ListenAsync` returns. Transports that omit this optional notification must finish their listen task before peer disposal completes.

## Connection protocol

Connection protocols implement `IConnectionProtocol`. They receive a channel from the lower protocol and can either select, transform, or multiplex the next protocol.

```csharp
public interface IConnectionProtocol : IProtocol
{
    Task ListenAsync(IChannel downChannel, IConnectionContext context);
    Task DialAsync(IChannel downChannel, IConnectionContext context);
}
```

Connection protocols typically do one or more of the following:

- choose one of `context.SubProtocols` and call `context.Upgrade(downChannel, selectedProtocol)` when negotiating protocols
- call `context.Upgrade()` and forward bytes when the protocol transforms the stream, such as encryption
- set `context.State.RemotePublicKey`, `context.State.RemoteAddress`, or other peer metadata discovered during the handshake. Build the key with `PublicKeyDecoder.Decode`, which validates it and returns secp256k1 keys in compressed form, so Identify and signed peer records compare against the same bytes
- call `context.UpgradeToSession()` when the connection is ready to become a libp2p session

For example, multistream-select negotiates the next protocol with `context.SubProtocols`, Noise and TLS authenticate and encrypt the channel, and Yamux creates the session and multiplexes application streams.

## Adding protocol to stack

If you develop a protocol for the standard libp2p stack, include it in `Libp2pPeerFactoryBuilder.BuildStack`. The builder creates protocol instances with `Get<TProtocol>()` and wires protocol layers with `Connect(...)`:

```csharp
protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
{
    ProtocolRef tcp = Get<IpTcpProtocol>();
    ProtocolRef selector = Get<MultistreamProtocol>();

    Connect([tcp], [selector], [Get<NoiseProtocol>(), Get<TlsProtocol>()], [selector], [Get<YamuxProtocol>()], [selector]);
    Connect([selector], [Get<PingProtocol>(), .. additionalProtocols]);

    return [tcp];
}
```

Application protocols can be added by application code with `AddAppLayerProtocol<TProtocol>()`.

## Cancellation and teardown

Connection protocols should use `IConnectionContext.Token` for their I/O and own all
forwarding tasks until they finish. The core aborts upgrade channels when either the
connection or the request is cancelled, including partially consumed writes.
`INewConnectionContext.Completion` lets a transport dispose the context and then join
its protocol upgrades before releasing connection admission. The default completion
is already complete for older custom contexts; implementations with background work
should supply their own completion task. Do not await completion from inside one of
that same connection's protocol upgrades.

A transport's connection cancellation registrations must be disposed when its pumps
stop. TCP cancels and joins both pumps, preserves one pooled scratch receive buffer
per pump, and publishes exact-sized owned copies. Downstream readers may retain
fragments after a channel write completes; never publish the pooled scratch array.
