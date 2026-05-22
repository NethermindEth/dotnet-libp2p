# Pubsub protocol set

This package implements the libp2p pubsub protocol family. It includes floodsub and gossipsub protocol handlers and provides a shared `PubsubRouter` for topic operations.

See the [libp2p pubsub spec](https://github.com/libp2p/specs/tree/master/pubsub) for protocol details.

## Setup

Enable pubsub on the standard stack with `WithPubsub()`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;

ServiceProvider provider = new ServiceCollection()
    .AddLibp2p(builder => builder.WithPubsub())
    .BuildServiceProvider();

ILocalPeer peer = provider.GetRequiredService<IPeerFactory>().Create();
await peer.StartListenAsync();

PubsubRouter router = provider.GetRequiredService<PubsubRouter>();
await router.StartAsync(peer);
```

`StartAsync` connects the router to the local peer and starts heartbeat and reconnect loops. Call it once for the peer that owns the router.

## Subscribing to a topic

`GetTopic` returns an `ITopic` and subscribes by default:

```csharp
ITopic chat = router.GetTopic("chat");

chat.OnMessage += (peerId, bytes) =>
{
    string text = Encoding.UTF8.GetString(bytes);
    Console.WriteLine($"{peerId}: {text}");
};
```

To create a topic handle without subscribing immediately, pass `subscribe: false` and call `Subscribe()` later:

```csharp
ITopic alerts = router.GetTopic("alerts", subscribe: false);
alerts.Subscribe();
```

## Publishing

Publish raw bytes or protobuf messages:

```csharp
chat.Publish(Encoding.UTF8.GetBytes("hello from dotnet-libp2p"));

// Any Google.Protobuf.IMessage can also be published.
// chat.Publish(myProtobufMessage);
```

Publishing does not create peers by itself. The router sends messages to connected pubsub peers and uses `PeerStore.OnNewPeer` to dial newly discovered peers that advertise pubsub support.

## Connecting pubsub peers

Use one of the discovery mechanisms to populate `PeerStore`, or add known peer addresses manually:

```csharp
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;

PeerStore peerStore = provider.GetRequiredService<PeerStore>();
peerStore.Discover([
    Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001/p2p/12D3KooW...")
]);
```

When a new peer is discovered, `PubsubRouter` dials it and negotiates the best supported pubsub protocol from the remote peer's identified protocols. Gossipsub v1.2 is preferred, then gossipsub v1.1, gossipsub v1.0, and floodsub.

## Unsubscribing

```csharp
chat.Unsubscribe();
router.UnsubscribeAll();
```

Unsubscribing announces the topic leave to connected pubsub peers and prunes the local mesh for that topic.
