# Application developer quick start

The libp2p network stack contains the pieces needed to work around common networking obstacles, find peers, connect to them, negotiate protocols, and exchange data. dotnet-libp2p lets you build custom transports and protocols, while the standard stack is available for rapid application development.

The following package links the core protocols and assembles them into a single convenient libp2p library:

```sh
dotnet add package Nethermind.Libp2p --prerelease
```

The library targets [.NET 10](https://dotnet.microsoft.com/en-us/download) or higher.

## Basic usage

.NET libp2p is IoC friendly. The `AddLibp2p` service collection extension allows basic customization:

- add user-defined protocols
- customize included protocols
- change libp2p core behavior

```cs
ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder.AddAppLayerProtocol<ChatProtocol>())
    .BuildServiceProvider();
```

One of the injected services is `IPeerFactory`. It creates peers that can wait for incoming connections:

```cs
IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
await using ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

peer.OnConnected += async remotePeer => Console.WriteLine("A peer connected {0}", remotePeer.RemoteAddress);

await peer.StartListenAsync();
```

Peers can also dial other peers:

```cs
ISession remotePeer = await localPeer.DialAsync(remoteAddr);
await remotePeer.DialAsync<SomeProtocol>();
```

Protocols can receive arguments and return data when they implement a typed session protocol:

```cs
ISession remotePeer = await localPeer.DialAsync(remoteAddr);
int answer = await remotePeer.DialAsync<SomeProtocol, string, int>("what is answer to the Ultimate Question?");
```

## Make a protocol for your application

1. Write your protocol

Libp2p protocols can be divided into 3 layers:

- Transport protocols use peer addresses to establish network connections.
- Connection protocols negotiate protocols, authenticate or encrypt traffic, create sessions, or multiplex streams.
- Application protocols rely on an established session and exchange the actual payload.

The protocol can be used to dial other peers or listen for connections.

Typically you need to implement `ISessionProtocol` or `ISessionProtocol<TRequest, TResponse>`:

```csharp
using Nethermind.Libp2p.Core;

// implement application layer protocol interface
class DeepThoughtProtocol : ISessionProtocol<string, int>
{
    // protocol id is required: libp2p peers negotiate protocols by id
    public string Id => "/deep-thought/2.0";

    // called when you dial a remote peer
    public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, string request)
    {
        // `downChannel` contains various methods to send and receive data
        // for example, you can write a line
        await downChannel.WriteLineAsync(request);
        // or read a variable-size integer
        return await downChannel.ReadVarintAsync();
    }

    // called when you listen and someone dials you
    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        string question = await downChannel.ReadLineAsync();
        await downChannel.WriteVarintAsync(question.GetHashCode());
    }
}
```

- The `downChannel` is used to receive data from and send data to the lower protocol layer.
- `context` holds information about local and remote peers. It allows more connections to be initiated within the current session.
- The current implementation for IChannel is Nethermind.Libp2p.Core.Channel and supports reading and writing by multiple threads

### Further exploration

- Add [logging and tracing](./logging-tracing.md)
- Check the samples directory. It includes chat apps, pubsub, discovery, and more.
- If the protocol is symmetric (i.e. listen and dial share the same logic), consider using `SymmetricProtocol` helper as base class. A remote peer does not need to use the same helper type; successful communication depends on both peers negotiating the same `ISessionProtocol.Id`.
- Need more information about other connected peers? Check `IPeerStore`.
- Conventions and other useful tips can be found in [the best practices list](./development/best-practices.md).
