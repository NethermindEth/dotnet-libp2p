# KadDHT Demo

This is a basic integration demo of the Kademlia Distributed Hash Table (KadDHT) core algorithm implementation in .NET libp2p.

## Overview

The Kademlia DHT is a distributed hash table that provides efficient lookup of values by keys in a peer-to-peer network. This demo showcases the **core Kademlia algorithm components** without full network transport.

## What This Demo Demonstrates

This simple console application demonstrates:

- ✅ **Kademlia component construction** - Shows how to wire together all required abstractions
- ✅ **Routing table management** - Creates a KBucketTree and populates it with nodes  
- ✅ **Node lookup algorithm** - Executes the k-nearest neighbor search algorithm
- ✅ **Simulated network transport** - Uses a mock message sender for testing
- ✅ **Dependency injection patterns** - Demonstrates proper abstraction usage

## What This Demo Does NOT Demonstrate

- ❌ **Real network transport** - Uses simulated responses, not actual libp2p protocols
- ❌ **Value storage/retrieval** - No `PutValue`/`GetValue` operations
- ❌ **Provider operations** - No content provider announcements or lookups
- ❌ **Protocol Buffers** - No actual message serialization/deserialization
- ❌ **Multi-peer networking** - Single-node simulation only
- ❌ **Bootstrap process** - No connection to real DHT networks

## Running the Demo

```bash
cd src/samples/kad-dht-demo
dotnet run
```

### Expected Output

```
Kademlia demo starting. Seeding table and running one lookup...
[13:45:32.123] dbug: DemoMessageSender[0] Simulated FindNeighbours to TestNode{...}: returned 2 nodes
[13:45:32.128] dbug: DemoMessageSender[0] Simulated FindNeighbours to TestNode{...}: returned 1 nodes  
[13:45:32.133] dbug: DemoMessageSender[0] Simulated FindNeighbours to TestNode{...}: returned 3 nodes
Lookup complete.
```

## Code Structure

The demo creates and configures all Kademlia components:

```csharp
// Core abstractions
IKeyOperator<PublicKey, ValueHash256, TestNode> keyOperator = new PublicKeyKeyOperator();
IKademliaMessageSender<PublicKey, TestNode> transport = new DemoMessageSender(logManager);
IRoutingTable<ValueHash256, TestNode> routingTable = new KBucketTree<ValueHash256, TestNode>(...);
ILookupAlgo<ValueHash256, TestNode> lookupAlgo = new LookupKNearestNeighbour<ValueHash256, TestNode>(...);

// Main Kademlia instance  
var kad = new Kademlia<PublicKey, ValueHash256, TestNode>(...);

// Seed routing table with random nodes
for (int i = 0; i < 64; i++)
{
    nodeHealthTracker.OnIncomingMessageFrom(new TestNode { Id = RandomPublicKey() });
}

// Execute one lookup to exercise the algorithm
_ = await kad.LookupNodesClosest(RandomPublicKey(), CancellationToken.None);
```

## Configuration

The demo uses these Kademlia parameters:

```csharp
KademliaConfig<TestNode> config = new()
{
    KSize = 16,        // K-bucket size (nodes per bucket)
    Alpha = 3,         // Lookup concurrency (parallel requests)  
    Beta = 2,          // Accelerated lookup parameter
};
```

## Mock Transport

The `DemoMessageSender` simulates network behavior:

- **5ms simulated latency** per request
- **0-3 random nodes** returned per `FindNeighbours` call
- **Proper async/await patterns** matching real transport
- **Logging integration** for observability

## Architecture Components

This demo exercises the following Kademlia implementation components:

| Component | Implementation | Purpose |
|-----------|----------------|---------|
| **Routing Table** | `KBucketTree<THash, TNode>` | Stores known peers in k-buckets |
| **Lookup Algorithm** | `LookupKNearestNeighbour<THash, TNode>` | Finds closest nodes to a target |
| **Key Operations** | `PublicKeyKeyOperator` | Handles key/hash conversions |
| **Node Health** | `NodeHealthTracker<TNode>` | Manages peer liveness |
| **Transport** | `DemoMessageSender` | Mock network message sender |


## Next Steps

1. **Real libp2p transport** with protocol negotiation (`/ipfs/kad/1.0.0`)
2. **Protocol Buffers messages** like for `PING`, `FIND_NODE`, `PUT_VALUE`.
3. **Value storage backend** for `GetValue`/`PutValue` operations  
4. **Provider record management** for content routing
5. **Bootstrap node discovery** and network joining
6. **Multi-peer testing** with real network behavior

## Related Documentation

- [Kademlia Paper](https://pdos.csail.mit.edu/~petar/papers/maymounkov-kademlia-lncs.pdf)
- [libp2p Kad-DHT Specification](https://github.com/libp2p/specs/tree/master/kad-dht)
- [Project Documentation](../../../README.md) 