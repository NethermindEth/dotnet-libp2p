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

The `peerId` argument is the connected peer that delivered the RPC message to the local router. It is not necessarily the original pubsub message author when messages are forwarded through the mesh. If your application needs author identity, include it in the published payload and validate it at the application layer.

To create a topic handle without subscribing immediately, pass `subscribe: false` and call `Subscribe()` later:

```csharp
ITopic alerts = router.GetTopic("alerts", subscribe: false);
alerts.Subscribe();
```

Only a current local subscription admits received full messages. A remote announcement, score configuration, publishing fanout, or an unsubscribed topic handle does not establish delivery interest. Unwanted payloads are ignored before message-ID calculation, signature or application validation, caches, delivery scoring, events, and forwarding. Subscriptions and control messages in the same RPC still run. Subscribed topics retain their default delivery rewards and invalid-message penalties even without explicit score parameters.

## Publishing

Publish raw bytes or protobuf messages:

```csharp
chat.Publish(Encoding.UTF8.GetBytes("hello from dotnet-libp2p"));

// Any Google.Protobuf.IMessage can also be published.
// chat.Publish(myProtobufMessage);
```

Publishing does not create peers by itself. The router sends messages to connected pubsub peers and uses `PeerStore.OnNewPeer` to dial newly discovered peers that advertise pubsub support.

## Validating received messages

`VerifyMessage` runs after libp2p signature verification and before the message is delivered, forwarded, or placed in the message cache. Its callback takes the **connected peer that sent the RPC** and the message. Applications using the older message-only callback must add the peer argument. Return `Accepted` to deliver and forward, `Rejected` to record an invalid delivery, `Ignored` to suppress without a penalty, or `Throttled` to drop without caching the message ID. Do not mutate the message in the callback.

For asynchronous checks, assign one `OnDeferredMessage` callback before configuring `VerifyMessage` to return `Deferred`, especially when the router is already running. The callback receives the same message instance outside the router lock. Complete it with `CompleteValidation(message, verdict)` before the timeout; a false return means the message is no longer pending, expired, or changed after authentication. A deferred message is not delivered or forwarded while pending.

```csharp
router.OnDeferredMessage = async (sourcePeer, message) =>
{
    bool valid = await ValidateAsync(sourcePeer, message);
    router.CompleteValidation(message, valid ? MessageValidity.Accepted : MessageValidity.Rejected);
};
router.VerifyMessage = (sourcePeer, message) => NeedsAsyncCheck(message)
    ? MessageValidity.Deferred
    : MessageValidity.Accepted;
```

Configure `MaxPendingValidationMessages`, `MaxPendingValidationBytes`, and `PendingValidationTimeout` in `PubsubSettings` to bound deferred work. `PendingValidationCount` reports the current unexpired count. If the store is full, the callback is absent, or the timeout is invalid, the router drops the deferred message without scoring its source because these are local capacity or configuration conditions. Expired entries are removed when the router next processes a message or queries the count; application validation work is not cancelled on expiry or disposal. An invalid signature is rejected before `VerifyMessage` and does not reserve its message ID, so replaying the same invalid message repeats signature verification.

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

When a new peer is discovered, `PubsubRouter` dials it and negotiates the best supported pubsub protocol from the remote peer's identified protocols. Gossipsub v1.3 is preferred, then gossipsub v1.2, gossipsub v1.1, gossipsub v1.0, and floodsub.

### Application-managed connections

If the application owns connection scheduling, peer limits or compatibility checks, register settings before resolving the router:

```csharp
services.AddSingleton(new PubsubSettings { AutoConnect = false });
```

With `AutoConnect` disabled, the router does not dial discovered peers (including previously stored addresses and PX), connect configured direct peers, or retry closed pubsub streams. Heartbeats and gossip on existing sessions continue. Identify, Identify Push and discovery still verify and store metadata; storing an address does not grant permission to connect. The default is `true`, preserving automatic connection management.

The application dials the transport, completes its checks, then opens a supported pubsub protocol on the admitted session. For example, if the peer advertises Gossipsub v1.3:

```csharp
Task gossipStream = admittedSession.DialAsync<GossipsubProtocolV13>(token);
```

The task lasts for the stream's lifetime: keep and observe it with the application's session tasks. The router opens a reply stream on the same session when the remote opens pubsub; this does not dial another transport connection. Disabling automatic connections is not an inbound protocol admission gate. Applications that require checks before serving inbound pubsub must enforce those checks in their protocol handlers. The application also owns reconnecting direct peers when automatic connections are disabled, including the explicit peering agreement's connectivity requirement.

This setting controls Pubsub only. Other enabled protocols that dial transports, such as Kad-DHT, need their own integration with the application's connection manager. No beacon-specific admission policy is part of Identify, the peer store or the session API.

## Unsubscribing

```csharp
chat.Unsubscribe();
router.UnsubscribeAll();
```

Unsubscribing announces the topic leave to connected pubsub peers and prunes the local mesh for that topic.
It also removes pending validations for the topic and releases their capacity; their later `CompleteValidation` calls return `false`, even after resubscribing. Already accepted callbacks queued for dispatch may finish concurrently with unsubscribe. Application validation work is not cancelled.

## Remote subscription limits

Remote membership is retained independently of local subscriptions so a later local subscription can discover peers. Configure these positive settings before constructing the router:

| Setting | Default | Scope |
| --- | --- | --- |
| `MaxRemoteSubscriptionsPerPeer` | 1,024 | Distinct retained topic memberships for one remote peer |
| `MaxRemoteSubscriptionTopicBytesPerPeer` | 1 MiB | Topic-name byte charges for one remote peer |
| `MaxRemoteSubscriptions` | 65,536 | Memberships across all remote peers |
| `MaxRemoteSubscriptionTopicBytes` | 64 MiB | Topic-name byte charges across all remote peers |

Each membership charges `2L * topic.Length` bytes for UTF-16 characters, including when multiple peers share a topic. Count limits also bound collection overhead; byte limits do not measure total managed-heap usage. Application-created local topics and historical scoring keep their existing lifetimes.

Announcements are processed in wire order. Per-peer limits always apply. At a global count or byte limit, a membership for a currently subscribed local topic can reclaim capacity by evicting the oldest unwanted memberships. Existing memberships on subscribed topics are protected. Local subscribe and unsubscribe update that priority without discarding remote announcements. The router checks that the entire new membership can fit before evicting anything; all count and byte limits remain hard bounds.

Other additions exceeding a budget are ignored without a penalty or disconnect; later entries, including unsubscribe, still run. Duplicate announcements consume no additional capacity and can update partial-message options. Rejected or evicted memberships require a new announcement to become known again, so discovery of topics without a local subscription is best effort at saturation. GRAFT's implicit membership uses the same priority and limits, and responds with PRUNE if it cannot fit. Saturation by subscribed memberships can still reject further wanted memberships; applications must size the limits for their active topics and peers.

Remote unsubscribe and disconnect release membership and partial-message metadata. Empty remote topic keys are removed from `IRoutingStateContainer.FloodsubPeers` and `GossipsubPeers`; local subscription alone no longer creates these keys. An active local subscription keeps its empty mesh so heartbeat can refill it. Local unsubscribe retains remote announcements, and publishing without subscribing continues to use fanout.

## Stopping the router

The router stops when the token passed to `StartAsync` is cancelled or when the router is disposed. Disposal cancels its heartbeat and reconnect loops and the dials it started, closes the pubsub streams it serves and stops reacting to new `PeerStore` peers. It does not dispose the `PeerStore` or the local peer.

```csharp
await router.DisposeAsync(); // waits for the owned background work to finish
```

A router resolved from the service provider is disposed together with the provider, so dispose it yourself only when you created it with `new`. `Dispose()` stops the same work without waiting for it. A disposed router cannot be started again.
