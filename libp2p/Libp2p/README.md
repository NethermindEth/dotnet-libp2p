# The libp2p stack

The entry point API for libp2p

Derive from `Libp2pPeerFactoryBuilder` to customize individual stack choices while
inheriting the standard negotiation order and wiring. Override `Identify`,
`IdentifyPush` or `Ping` to replace a core protocol, or return `null` to omit it.
Automatic Identify and Push dials use the installed protocol's standard wire
identifier. Replacements should keep that identifier and implement
`ISessionProtocol` for Identify or `ISessionProtocol<ulong, ulong>` for Push.
Omitting either protocol also omits its automatic dials.
Override `StreamTransports`, `DatagramTransports`, `Encryption`, `Muxers`, `Relay`
or `Pubsub` to return the protocols offered by that layer, in order.
Datagram transports connect directly to the application selector; stream
transports use security and multiplexing negotiation. An empty transport set
omits that transport family. An empty security or multiplexer set leaves no
protocol to negotiate on stream transports.

For example, this builder omits Ping and offers only TCP, keeping the rest of
the standard stack:

```csharp
public class TcpPeerFactoryBuilder(IServiceProvider provider)
    : Libp2pPeerFactoryBuilder(provider)
{
    protected override ProtocolRef? Ping() => null;
    protected override ProtocolRef[] StreamTransports() => [Get<IpTcpProtocol>()];
    protected override ProtocolRef[] DatagramTransports() => [];
}
```

Register a derived builder with `services.AddLibp2p<TcpPeerFactoryBuilder>()`.
The existing `With*` options continue to control the base hook implementations.
