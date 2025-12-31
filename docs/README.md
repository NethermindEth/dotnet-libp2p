# Application developers quick start

Libp2p network stack includes all necessary to bypass typical networking obstacles, find peers, connect to them and exchange data according to the selected protocol. The implementation allows to build your own transport, but standard stack can be used for rapid development.

The following package includes links to protocols and provides them assembled in a single convenient LibP2P library:

```
dotnet add package Nethermind.Libp2p --prerelease
```

The library requires [.NET 8](https://dotnet.microsoft.com/en-us/download) or higher

## Basic usage

.NET libp2p is IoC friendly. `AddLibp2p` service collection extension allows to provide basic customization:
- add user define protocols
- customize included ones
- change libp2p core behavior

```cs
ServiceProvider serviceProvider = new ServiceCollection()
    .AddLibp2p(builder => builder.AddAppLayerProtocol<ChatProtocol>())
    .BuildServiceProvider();
```

One of the injected services is `IPeerFactory`. It allows peer to be created that can wait for connections:

```cs
IPeerFactory peerFactory = serviceProvider.GetService<IPeerFactory>()!;
await using ILocalPeer peer = peerFactory.Create(optionalFixedIdentity);

peer.OnConnected += async remotePeer => Console.WriteLine("A peer connected {0}", remotePeer.RemoteAddress);

await peer.StartListenAsync();
```

It can also dial other peers:

```cs
ISession remotePeer = await localPeer.DialAsync(remoteAddr);
await remotePeer.DialAsync<SomeProtocol>();
```

When properly implemented protocols may receive arguments and return data:

```cs
ISession remotePeer = await localPeer.DialAsync(remoteAddr);
int answer = await remotePeer.DialAsync<SomeProtocol, string, int>("what is answer to the Ultimate Question?");
```

## Make a protocol for your application

1. Write your protocol

Libp2p protocols can be divided into 3 layers:
- Transport layer protocols, that actively use peer address to discover network and establish connection
- Connection layer protocols
- Application layer protocols, that rely on an established session and are used to exchange an actual payload

The protocol can be used to dial other peers or listen for connections.

Typically you need to implement `ISessionProtocol` or `ISessionProtocol<TRequest, TResponse>`:

```csharp
using Nethermind.Libp2p.Core;

// implement application layer protocol interface
class DeepThoughtProtocol : ISessionProtocol<string, int>
{
    // protocol id is required: libp2p exchange with protocol ids at some step
    public string Id => "/deep-thought/2.0";

    // called when you dial a remote peer
    public async Task<int> DialAsync(IChannel downChannel, ISessionContext context, string request)
    {
        // `downChannel` contains various method to send and receive data
        // you can write a string with \n at the end
        await downChannel.WriteLineAsync(request);
        // or read an integer
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

- The `downChannel` is used to receive from and send data to the transport layer.
- `context` holds information about local and remote peers. It allows more connections to be initiated within the current session.
- The current implementation for IChannel is Nethermind.Libp2p.Core.Channel and supports reading and writing by multiple threads

### Further exploration
- Add [logging and tracing](./logging-tracing.md)
- Go check samples dir! It include chat apps, pubsub, discovery and more
- If the protocol is symmetric (i.e. listen and dial share the same logic), consider using `SymmetricProtocol` helper as base class.
- Need more information about other connected peers - check `IPeerStore`
- Conventions and more potentially useful tips can be found in [the best practices list](./development/best-practices.md)
