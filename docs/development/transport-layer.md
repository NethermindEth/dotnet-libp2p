# Developing a transport layer protocol

Transport layer protocols are responsible for:
- connecting to peers: `ip`, `tcp`, `udp`;
- protocol negotiation: `multistream-select`, `mplex`, `yamux`;
- encryption: `noise`, `tls`.

It may cover several aspects also, like `quic` does.

The usual routine of a protocol is to:
- Wait for connection by listening or dial actively;
- Make a handshake;
- Start an upper layer protocol and redirect data to it.

Let's dig into details.

### Initiating connection

Each transport layer protocol should implement `IId`,`IDialer`,`IListener` interfaces, which enforce the `Listen/Dial` pattern. A shortcut for that set of interfaces is `IDuplexProtocol`.

```csharp
namespace Nethermind.Libp2p.Core;

public class MyCustomProtocol : IDuplexProtocol
{
    public string Id => "/my-custom-tl-protocol/1.0.0";
    
    public Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        ...
    }

    public Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        ...
    }
}
```

Protocol may wait for connection from `downChannel` or manage connection itself on the root level(in this case `downChannel` should be used as a ruling channel that may signal about closing when it's requested by user).

### Protocol negotiation and starting up layer protocol

Handshake according to the protocol can reveal peers incompatibility and the connection can be closed by closing `downChannel`.
Otherwise, `upChannelFactory` can be used to start upper layer protocol. If the protocol is used to select between several upper layer protocols, choose them from `upChannelFactory.SubProtocols` or check for `upChannelFactory.SpecificProtocolRequest` that may come from a layer below.

## Adding protocol to stack

In case you develop for libp2p protocol, you may want to include it in `Libp2pPeerFactoryBuilder`. Otherwise, you can create your own stack.

The stack can be defined by chaining protocols using `Over/Or/AddAppLayerProtocol`:

```csharp
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

public class APeerFactoryBuilder : PeerFactoryBuilderBase<APeerFactoryBuilder, PeerFactory>
{
    protected override APeerFactoryBuilder BuildTransportLayer()
    {
        return Over<IpTcpProtocol>()                  // use a regular protocol
            .Over<MultistreamProtocol>()              // add a protocol that can select from several ones on top of it
            .Over<NoiseProtocol>()                    // a protocol to select from
            .Or<PlainTextProtocol>()                  // another one to select from
            .Over<YamuxProtocol>()                    // next one on top of previously selected during negotiation
            .AddAppLayerProtocol<MyCustomProtocol>(); // an inbuilt applayer protocol
    }
}
```
