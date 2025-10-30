# Kad-DHT Implementation Context
w, plea

## Current Implementation Architecture

### Directory Structure
```
src/libp2p/Libp2p.Protocols.KadDht/
├── Dto/                           # Protocol message definitions
│   └── Kademlia.proto            # Protobuf definitions
├── Kademlia/                     # Core algorithm implementation (20+ files)
│   ├── IKademlia.cs             # Main interface
│   ├── Kademlia.cs              # Core implementation
│   ├── IRoutingTable.cs         # K-bucket routing
│   ├── KBucketTree.cs           # Binary tree of K-buckets
│   ├── ILookupAlgo.cs           # Node lookup algorithms
│   └── ...                      # Hash utilities, node health, etc.
├── Session/                      # Session management
│   ├── ISessionManager.cs       # Session lifecycle
│   ├── KademliaSessionManager.cs # Implementation
│   └── IKademliaNodeAdapter.cs  # Node adaptation
├── Transport/                    # Network transport layer
│   ├── IKademliaMessageSender.cs # Network messaging interface
│   ├── KadDhtProtocolExtensions.cs # Builder registration
│   └── KademliaMessageSender.cs # Message transport implementation
└── Tests/                        # Test project
    └── KadDhtProtocolTests.cs   # Main protocol tests (references missing KadDhtProtocol)
```

## Key Interfaces & Implementations

### Core Kademlia Interface
```csharp
public interface IKademlia<TPublicKey, TNode>
{
    void AddOrRefresh(TNode node);
    void Remove(TNode node);
    Task Run(CancellationToken token);
    Task Bootstrap(CancellationToken token);
    Task<TNode[]> LookupNodesClosest(TPublicKey key, CancellationToken token, int? k = null);
    TNode[] GetKNeighbour(TPublicKey target, TNode? excluding = default, bool excludeSelf = false);
    event EventHandler<TNode> OnNodeAdded;
    IEnumerable<TNode> IterateNodes();
}
```

### Transport Interface
```csharp
public interface IKademliaMessageSender<TTargetKey, TNode>
{
    Task<TNode[]> FindNeighbours(TNode receiver, TTargetKey target, CancellationToken token);
    Task Ping(TNode receiver, CancellationToken token);
}
```

### Current Protocol Messages (Kademlia.proto)
```protobuf
message PingRequest {}
message PingResponse {}
message FindNeighboursRequest { PublicKeyBytes target = 1; }
message FindNeighboursResponse { repeated Node neighbours = 1; }
message Node {
    bytes publicKey = 1;
    repeated string multiaddrs = 2;
}
```

## Missing Implementation Components

### 1. Main Protocol Class (CRITICAL)
**File**: `src/libp2p/Libp2p.Protocols.KadDht/KadDhtProtocol.cs`
**Status**: Referenced in tests but doesn't exist
**Expected Interface**:
```csharp
public class KadDhtProtocol : ISessionProtocol
{
    public string Id => "/ipfs/kad/1.0.0";

    // Constructor expects: ILocalPeer, ILoggerFactory, KadDhtOptions
    public KadDhtProtocol(ILocalPeer localPeer, ILoggerFactory loggerFactory, KadDhtOptions options);

    // Expected public API (from tests):
    Task<bool> PutValueAsync(byte[] key, byte[] value, CancellationToken token);
    Task<byte[]> GetValueAsync(byte[] key, CancellationToken token);
    Task<bool> ProvideAsync(byte[] key, CancellationToken token);
    Task<IEnumerable<PeerId>> FindProvidersAsync(byte[] key, int count, CancellationToken token);

    // ISessionProtocol implementation:
    Task DialAsync(IChannel channel, ISessionContext context);
    Task ListenAsync(IChannel channel, ISessionContext context);
}
```

### 2. DHT Value Operations
**Messages Needed**:
```protobuf
message PutValueRequest {
    bytes key = 1;
    bytes value = 2;
    bytes signature = 3;  // For value validation
    int64 timestamp = 4;  // For freshness
}
message PutValueResponse {
    bool success = 1;
    string error = 2;
}
message GetValueRequest {
    bytes key = 1;
}
message GetValueResponse {
    bytes value = 1;
    bool found = 2;
    bytes signature = 3;
    int64 timestamp = 4;
}
```

### 3. Provider Records
**Messages Needed**:
```protobuf
message AddProviderRequest {
    bytes key = 1;
    bytes provider_id = 2;  // PeerId bytes
    repeated string multiaddrs = 3;
}
message AddProviderResponse {
    bool success = 1;
}
message GetProvidersRequest {
    bytes key = 1;
    int32 count = 2;
}
message GetProvidersResponse {
    repeated Provider providers = 1;
}
message Provider {
    bytes peer_id = 1;
    repeated string multiaddrs = 2;
}
```

### 4. Configuration & Options
**File**: `src/libp2p/Libp2p.Protocols.KadDht/KadDhtOptions.cs`
```csharp
public class KadDhtOptions
{
    public int KSize { get; set; } = 20;        // K-bucket size
    public int Alpha { get; set; } = 3;         // Concurrency parameter
    public KadDhtMode Mode { get; set; } = KadDhtMode.Server;
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan RecordTtl { get; set; } = TimeSpan.FromHours(24);
}

public enum KadDhtMode
{
    Client,  // Don't store records, only query
    Server   // Store records and respond to queries
}
```

## Protocol ID Mapping
- **Base**: `/ipfs/kad/1.0.0`
- **Ping**: `/ipfs/kad/1.0.0/ping`
- **FindNeighbours**: `/ipfs/kad/1.0.0/findneighbours`
- **PutValue**: `/ipfs/kad/1.0.0/putvalue` (to implement)
- **GetValue**: `/ipfs/kad/1.0.0/getvalue` (to implement)
- **AddProvider**: `/ipfs/kad/1.0.0/addprovider` (to implement)
- **GetProviders**: `/ipfs/kad/1.0.0/getproviders` (to implement)

## Integration Points

### Between Algorithm & Transport
The core `IKademlia` implementation needs to be connected to the libp2p transport layer through:
1. **Message Sender Implementation** - Convert `IKademliaMessageSender` calls to libp2p protocol calls
2. **Session Management** - Handle protocol sessions and multiplexing
3. **Node Adaptation** - Convert between Kademlia node types and libp2p peer identities

### Storage Layer Integration
The DHT needs persistent/semi-persistent storage for:
1. **Value Store** - Key-value records with TTL and validation
2. **Provider Store** - Content provider mappings with TTL
3. **Routing Table** - Persistent peer information across restarts

## Development Priorities

### Phase 1: Core Protocol (Current Focus)
1. ✅ Core Kademlia algorithm (completed)
2. ❌ Create `KadDhtProtocol` class implementing `ISessionProtocol`
3. ❌ Add missing protobuf messages (PUT_VALUE, GET_VALUE, etc.)
4. ❌ Implement basic value storage in-memory

### Phase 2: Advanced Features
1. Provider record system
2. Value validation and signatures
3. Record TTL and cleanup
4. Bootstrap and network maintenance

### Phase 3: Production Readiness
1. Persistent storage backends
2. Performance optimizations
3. Security hardening
4. Comprehensive error handling

## Test Strategy
The test file `KadDhtProtocolTests.cs` shows expected behavior:
- Protocol ID should be `/ipfs/kad/1.0.0`
- `PutValueAsync()` stores values locally and returns success
- `GetValueAsync()` retrieves previously stored values
- `ProvideAsync()` registers local peer as provider
- `FindProvidersAsync()` returns registered providers

These tests provide the contract for implementation.

## Key Implementation Notes

### Node Type Mapping
- **Kademlia Layer**: Uses generic `TNode` type with `TPublicKey`
- **libp2p Layer**: Uses `PeerId` and `ISession` types
- **Bridge Layer**: Needs adapters to convert between these representations

### Message Flow
1. **Incoming**: `ISessionProtocol.ListenAsync` → Parse protocol message → Route to Kademlia layer
2. **Outgoing**: Kademlia operation → `IKademliaMessageSender` → Protocol session → Network

### Error Handling Strategy
- Network errors: Retry with exponential backoff
- Validation errors: Reject and log
- Storage errors: Attempt recovery or graceful degradation
- Timeout handling: Use CancellationToken throughout

This context provides the detailed foundation needed to implement the missing Kad-DHT components.
