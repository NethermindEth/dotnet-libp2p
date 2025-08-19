# KadDHT Demo

This is a demonstration of the Kademlia Distributed Hash Table (KadDHT) protocol implementation in libp2p.

## Overview

The Kademlia DHT is a distributed hash table that provides efficient lookup of values by keys in a peer-to-peer network. It is used in libp2p for:

- Content routing (finding which peers have a specific piece of content)
- Peer routing (finding peers by their ID)
- Value storage and retrieval (storing and retrieving arbitrary data)

## Features

This demo application demonstrates the following features:

- Connecting to other peers
- Storing values in the DHT
- Retrieving values from the DHT
- Announcing that you provide a specific key
- Finding providers for a specific key
- Finding peers closest to a specific key

## Usage

### Running the Demo

```bash
dotnet run
```

### Commands

Once the application is running, you can use the following commands:

- `connect <multiaddr>` - Connect to a peer using its multiaddress
- `put <key> <value>` - Store a value in the DHT
- `get <key>` - Retrieve a value from the DHT
- `provide <key>` - Announce that you provide a key
- `find-providers <key>` - Find providers for a key
- `find-peers <key>` - Find peers closest to a key
- `exit` - Exit the application

### Example Session

```
KadDHT Demo
===========
Local peer ID: 12D3KooWRMeUdkn4QKr8XWrGRKXzgMgZUgPMrwZbDwxd1G2voXqH
Listening on: /ip4/127.0.0.1/tcp/50001, /ip4/192.168.1.100/tcp/50001

Available commands:
  connect <multiaddr> - Connect to a peer
  put <key> <value> - Store a value in the DHT
  get <key> - Retrieve a value from the DHT
  provide <key> - Announce that you provide a key
  find-providers <key> - Find providers for a key
  find-peers <key> - Find peers closest to a key
  exit - Exit the application

> connect /ip4/192.168.1.101/tcp/50001/p2p/12D3KooWJLpZF9LgQa92Pt3CtMEsJQPuEkSvYPGjjQTjkdPyeg8K
Connecting to /ip4/192.168.1.101/tcp/50001/p2p/12D3KooWJLpZF9LgQa92Pt3CtMEsJQPuEkSvYPGjjQTjkdPyeg8K...
Connected to peer 12D3KooWJLpZF9LgQa92Pt3CtMEsJQPuEkSvYPGjjQTjkdPyeg8K

> put hello world
Putting value for key 'hello'...
Value stored successfully

> get hello
Getting value for key 'hello'...
Value: world

> provide hello
Announcing provider for key 'hello'...
Provider announced successfully

> find-providers hello
Finding providers for key 'hello'...
Found 2 providers:
  12D3KooWRMeUdkn4QKr8XWrGRKXzgMgZUgPMrwZbDwxd1G2voXqH
  12D3KooWJLpZF9LgQa92Pt3CtMEsJQPuEkSvYPGjjQTjkdPyeg8K
```

## Implementation Details

This demo uses the libp2p KadDHT protocol implementation, which follows the [libp2p Kademlia DHT specification](https://github.com/libp2p/specs/tree/master/kad-dht).

The KadDHT protocol is implemented as a libp2p protocol that can be added to a libp2p host using the `AddKadDht` extension method:

```csharp
services.AddLibp2p(builder =>
{
    builder.AddKadDht(options =>
    {
        options.EnableServerMode = true;
        options.EnableClientMode = true;
        options.EnableValueStorage = true;
        options.EnableProviderStorage = true;
        options.BucketSize = 20;
        options.Alpha = 3;
    });
});
```

The implementation includes:
- A Kademlia routing table
- DHT value storage and retrieval
- Provider record management
- Peer discovery and routing

## Network Protocol

The KadDHT protocol uses Protocol Buffers for message serialization and follows the libp2p protocol negotiation flow. The protocol ID is `/ipfs/kad/1.0.0` by default.

Messages include:
- PING - Check if a peer is alive
- FIND_NODE - Find nodes closest to a key
- GET_VALUE - Get a value from the DHT
- PUT_VALUE - Store a value in the DHT
- ADD_PROVIDER - Announce that you provide a key
- GET_PROVIDERS - Find providers for a key 