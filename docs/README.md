# Application developers quick start

Libp2p network stack includes all necessary to bypass typical networking obstacles, find peers, connect to them and exchange data according to the selected protocol. The implementation allows to build your own transport, but standard stack can be used for rapid development.

The current implementation is WIP and is not available via nuget yet. To build an app, you can utilize [chat example](../src/samples/chat/README.md).

## Prerequisites

Development requires [.NET 7 SDK](https://dotnet.microsoft.com/en-us/download)

## Make a protocol for your application

1. Write your protocol

Libp2p protocols can be divided into 2 layers:
- Transport layer protocols, that actively use peer address to discover network and establish connection;
- Application layer protocols, that is used to exchange actual payload once the connection is active.

Protocol can be used to dial to other peer or to listen for connections.

So when a p2p communication needs to be implemented, it's required to implement a protocol according to `IProtocol` interface:

```csharp
namespace Nethermind.Libp2p.Core;

public abstract class MyCustomProtocol
{
    public Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        ...
    }

    public Task ListenAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        ...
    }
}
```

The `downChannel` is used to receive from and send data to the transport layer. Check `downChannel.Reader` and `downChannel.Writer`'s methods. You need to close this channel if communication is finished.

`upChannelFactory` is mostly used by transport layer protocols to initiate upper layer protocol communication.

`context` holds information about peers.

If protocol symmetric, consider using `SymmetricProtocol` helper as base class.

2. When protocol is defined, you need add it to the stack and create factory:

```csharp
using Nethermind.Libp2p.Builder;
using Nethermind.Libp2p.Core;

IPeerFactory peerFactory = Libp2pPeerFactoryBuilder.Instance
    .AddAppLayerProtocol<MyCustomProtocol>()
    .Build();
```

It can be added as an instance or as a type parameter.

3. To dial to a peer with your new protocol, you need to instantiate a local peer that holds identity using the factory, and then dial. You need to dial to establish connection to remote peer and then dial with your protocol:

```csharp
    ... 
    ILocalPeer localPeer = peerFactory.Create();

    IRemotePeer remotePeer = await localPeer.DialAsync(remoteAddr);
    await remotePeer.DialAsync<ChatProtocol>(ts.Token);
    await remotePeer.DisconnectAsync();
    ...
```

when for listening, the protocol will be automatically negotiated, as far as it was defined while building the stack:

```csharp
    ... 
    ILocalPeer peer = peerFactory.Create();

    IListener listener = await peer.ListenAsync(
        $"/ip4/0.0.0.0/tcp/3000/p2p/{peer.Identity.PeerId}");
    listener.OnConnection += async remotePeer => Console.WriteLine($"A peer connected {remotePeer.Address}");
    ...
```

4. Conventions and potentially useful tips can be found in [the best practices list](./development/best-practices.md)
