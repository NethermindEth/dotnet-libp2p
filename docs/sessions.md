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
