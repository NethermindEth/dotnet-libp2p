# .NET libp2p Project Context

## Project Overview
This is a comprehensive implementation of the libp2p networking stack in .NET, focusing on the Kademlia DHT protocol implementation.

## Key Architecture Components

### Core Interfaces
- `ISessionProtocol` - Base protocol interface for bidirectional sessions
- `ISessionProtocol<TRequest, TResponse>` - Request-response protocol pattern
- `IChannel` - Core communication abstraction (IReader + IWriter)  
- `ILocalPeer` - Represents the local peer with identity and networking
- `ISession` - Represents a connection to a remote peer

### Protocol Implementation Patterns

#### 1. Simple Session Protocol Pattern
```csharp
public class [Protocol]Protocol : ISessionProtocol
{
    public string Id => "/ipfs/[protocol]/1.0.0";
    public Task DialAsync(IChannel channel, ISessionContext context) { /* client logic */ }
    public Task ListenAsync(IChannel channel, ISessionContext context) { /* server logic */ }
}
```

#### 2. Request-Response Protocol Pattern  
```csharp
public class RequestResponseProtocol<TRequest, TResponse> : ISessionProtocol<TRequest, TResponse>
    where TRequest : IMessage<TRequest>, new()
    where TResponse : IMessage<TResponse>, new()
{
    // Automatic protobuf serialization/deserialization
    // Size-prefixed message handling via channel.ReadPrefixedProtobufAsync()
    // Type-safe request/response processing
}
```

#### 3. Protocol Registration Pattern
```csharp
// Extension method in [Protocol]ProtocolExtensions
public static IPeerFactoryBuilder Add[Protocol]Protocol(this IPeerFactoryBuilder builder)
{
    return builder.AddRequestResponseProtocol<TRequest, TResponse>(
        protocolId, 
        requestHandler,
        isExposed: true
    );
}
```

### Project Structure
```
src/libp2p/
├── Libp2p.Core/                    # Core abstractions & interfaces
├── Libp2p/                         # Main aggregation assembly
├── Libp2p.Protocols.[Name]/        # Individual protocol implementations
│   ├── [Protocol]Protocol.cs       # Main protocol implementation
│   ├── [Protocol]ProtocolExtensions.cs # Builder extensions
│   └── Dto/                        # Protobuf message definitions
├── Libp2p.Protocols.[Name].Tests/  # Protocol-specific tests
└── Libp2p.Generators.*/           # Code generation tools
```

## Kad-DHT Implementation Status

### Completed Components
- **Core Kademlia Algorithm** (`Kademlia/` directory - 20+ files)
  - `IKademlia<TPublicKey, TNode>` - Main interface  
  - `Kademlia<TPublicKey, THash, TNode>` - Concrete implementation
  - `KBucketTree` - Binary tree of K-buckets with XOR distance
  - `IRoutingTable<THash, TNode>` - K-bucket routing table
  - `ILookupAlgo` - Node lookup algorithms
  - Complete hash utilities and key operations

- **Protocol Definitions** (`Dto/Kademlia.proto`)
  - PingRequest/PingResponse
  - FindNeighboursRequest/FindNeighboursResponse  
  - Node message structure

- **Transport Infrastructure** (`Transport/`)
  - `IKademliaMessageSender<TTargetKey, TNode>` interface
  - `KadDhtProtocolExtensions` with builder methods
  - Protocol registration for ping and findneighbours

- **Session Management** (`Session/`)
  - Session management interfaces and implementations

### Missing Components (Priority Order)
1. **Main KadDhtProtocol class** - Referenced in tests but doesn't exist
2. **DHT Value Operations** - PUT_VALUE/GET_VALUE messages and storage
3. **Provider Records** - ADD_PROVIDER/GET_PROVIDERS for content routing  
4. **Integration Layer** - Bridge between Kademlia algorithm and libp2p sessions
5. **Complete Message Set** - Missing DHT-specific protocol messages

### Protocol IDs Used
- Base: `/ipfs/kad/1.0.0`
- Ping: `/ipfs/kad/1.0.0/ping`
- FindNeighbours: `/ipfs/kad/1.0.0/findneighbours`

## Development Conventions

### Naming
- **Namespace**: `Nethermind.Libp2p.*` (main) / `Libp2p.Protocols.KadDht.*` (kad-dht)
- **Assembly**: `Nethermind.$(MSBuildProjectName)`
- **License**: MIT (main) / LGPL-3.0 (kad-dht specific)

### Code Style
- Modern C# with latest language features
- Nullable reference types enabled
- Async/await throughout with CancellationToken support
- Memory-efficient: `ReadOnlySequence<byte>`, `Span<T>`, `Memory<T>`
- Structured logging with Microsoft.Extensions.Logging

### Dependencies
- **Google.Protobuf** - Message serialization
- **Microsoft.Extensions.*** - DI, logging, hosting
- **BouncyCastle.Cryptography** - Cryptographic operations
- **NUnit** - Testing framework
- **NSubstitute** - Mocking framework

### Testing Patterns
- Individual test projects per protocol (`*.Tests.csproj`)  
- `Libp2p.Core.TestsBase` for shared utilities
- End-to-end tests (`*.E2eTests.csproj`)
- Test-driven development encouraged

## Build & Development

### Target Framework
- .NET 8.0/9.0 with latest C# language version
- Cross-platform (Windows/Linux/macOS)

### Solution Structure
- Single solution file: `Libp2p.sln`
- Clear project hierarchy and dependencies
- Consistent package versioning via Directory.Build.props

### Key Extension Methods
- `IPeerFactoryBuilder.AddProtocol<T>()` - Register simple protocols
- `IPeerFactoryBuilder.AddRequestResponseProtocol<TRequest, TResponse>()` - Register RPC protocols  
- `IChannel.ReadPrefixedProtobufAsync()` - Read size-prefixed protobuf messages
- `IChannel.WriteSizeAndProtobufAsync()` - Write size-prefixed protobuf messages

## Critical Implementation Notes

### Channel Operations
- Always use size-prefixed reads/writes for protobuf messages
- Handle `ReadResult.Result` for error checking
- Use `ReadBlockingMode.WaitAll` for exact length reads
- Proper buffer lifecycle management

### Protocol Registration
- Use extension methods in `[Protocol]ProtocolExtensions` class
- Follow naming convention: `Add[Protocol]Protocol()`  
- Provide `isExposed` parameter for protocol visibility
- Register sub-protocols with base ID + sub-path

### Error Handling
- `IOResult` enum for operation results
- Exception-based error propagation in protocols
- Comprehensive logging with performance checks
- Cancellation token support throughout

This context provides the foundation for intelligent development assistance on the .NET libp2p project.