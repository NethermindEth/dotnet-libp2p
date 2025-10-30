# .NET libp2p Project Index
*Generated: 2025-01-20*

## Project Overview

**dotnet-libp2p** is a comprehensive .NET implementation of the libp2p networking stack, focusing on building peer-to-peer applications with production-ready protocols.

- **Repository**: https://github.com/NethermindEth/dotnet-libp2p
- **License**: MIT
- **Target Framework**: .NET 8.0/9.0
- **Status**: Beta - Under active development

## Solution Structure

```
dotnet-libp2p/
├── src/
│   ├── libp2p/                          # Core library projects
│   └── samples/                         # Sample applications
├── docs/                                # Documentation
├── devnotes/                            # Development notes and test projects
├── .claude/                             # Claude AI context files
└── README.md                            # Main project README
```

## Core Library Projects

### Foundation Layer

#### **Libp2p.Core** - Core Abstractions
**Location**: `src/libp2p/Libp2p.Core/`
**Purpose**: Core interfaces, abstractions, and types for the entire libp2p stack

**Key Components**:
- `IChannel` - Core I/O abstraction (IReader + IWriter)
- `ILocalPeer` - Local peer identity and networking
- `ISession` - Connection to remote peer
- `IProtocol` - Base protocol interface
- `IPeerFactory` / `IPeerFactoryBuilder` - Peer creation and configuration
- `Identity` / `PeerId` - Cryptographic identity management
- `Multiaddress` extensions - Network address handling

**Key Directories**:
- `Context/` - Session and connection contexts
- `Discovery/` - Content routing and peer discovery interfaces
- `Dto/` - Protocol Buffers definitions (KeyPair, PeerRecord, SignedEnvelope)
- `Enums/` - Multiformat enumerations (Multiaddr, Multihash, etc.)
- `Exceptions/` - Custom exception types
- `Extensions/` - Utility extension methods
- `Utils/` - Helper utilities

**Tests**: `Libp2p.Core.Tests/`, `Libp2p.Core.TestsBase/`

#### **Libp2p** - Main Library Aggregation
**Location**: `src/libp2p/Libp2p/`
**Purpose**: Main entry point, aggregates all protocols and provides builder patterns

**Key Files**:
- `Libp2pPeerFactory.cs` - Peer factory implementation
- `Libp2pPeerFactoryBuilder.cs` - Fluent builder for peer configuration
- `ServiceProviderExtensions.cs` - DI integration

### Code Generation

#### **Libp2p.Generators.Protobuf**
**Location**: `src/libp2p/Libp2p.Generators.Protobuf/`
**Purpose**: Source generator for Protocol Buffers (converts .proto files to C# classes)

#### **Libp2p.Generators.Enums**
**Location**: `src/libp2p/Libp2p.Generators.Enums/`
**Purpose**: Source generator for multiformat enumerations

### Observability

#### **Libp2p.OpenTelemetry**
**Location**: `src/libp2p/Libp2p.OpenTelemetry/`
**Purpose**: OpenTelemetry integration for metrics and tracing

## Protocol Implementations

### Transport Protocols

#### **Libp2p.Protocols.IpTcp** ✅
**Protocol ID**: `/tcp`
**Status**: Production Ready
**Purpose**: TCP transport layer
**File**: `IpTcpProtocol.cs`

#### **Libp2p.Protocols.Quic** 🚧
**Protocol ID**: `/quic-v1`
**Status**: Work in Progress
**Purpose**: QUIC transport protocol
**Key Files**:
- `QuicProtocol.cs` - Main QUIC implementation
- `CertificateHelper.cs` - TLS certificate management
**Tests**: `Libp2p.Protocols.Quic.Tests/`

### Security Protocols

#### **Libp2p.Protocols.Noise** ✅
**Protocol ID**: `/noise`
**Status**: Production Ready
**Purpose**: Noise protocol framework for secure channels
**Files**:
- `NoiseProtocol.cs` - Noise protocol implementation
- `Dto/Exchange.proto` - Handshake message definitions
**Tests**: `Libp2p.Protocols.Noise.Tests/`

#### **Libp2p.Protocols.Plaintext** ✅
**Protocol ID**: `/plaintext/2.0.0`
**Status**: Production Ready (Development Only)
**Purpose**: Unencrypted plaintext connections (for testing)
**Files**:
- `PlainTextProtocol.cs`
- `Dto/Exchange.proto`

#### **Libp2p.Protocols.Tls** 🚧
**Protocol ID**: `/tls/1.0.0`
**Status**: Work in Progress
**Purpose**: TLS 1.3 security protocol
**Tests**: `Libp2p.Protocols.Tls.Tests/`

### Stream Multiplexing

#### **Libp2p.Protocols.Yamux** ✅
**Protocol ID**: `/yamux/1.0.0`
**Status**: Production Ready
**Purpose**: Stream multiplexing protocol
**Key Files**:
- `YamuxProtocol.cs` - Main protocol
- `YamuxHeader.cs` - Frame header handling
- `LocalDataWindow.cs` / `RemoteDataWindow.cs` - Flow control
- `ChannelState.cs` - Stream state management
**Tests**: `Libp2p.Protocols.Yamux.Tests/`

#### **Libp2p.Protocols.Multistream** ✅
**Protocol ID**: `/multistream/1.0.0`
**Status**: Production Ready
**Purpose**: Protocol negotiation and selection
**File**: `MultistreamProtocol.cs`
**Tests**: `Libp2p.Protocols.Multistream.Tests/`

### Application Protocols

#### **Libp2p.Protocols.Identify** ✅
**Protocol IDs**: `/ipfs/id/1.0.0`, `/ipfs/id/push/1.0.0`
**Status**: Production Ready
**Purpose**: Peer identification and capability discovery
**Key Files**:
- `IdentifyProtocol.cs` - Main identify protocol
- `IdentifyPushProtocol.cs` - Push-based updates
- `IdentifyNotifier.cs` - Event notifications
- `Dto/Identify.proto` - Protocol messages
**Tests**: Covered in E2E tests

#### **Libp2p.Protocols.Ping** ✅
**Protocol ID**: `/ipfs/ping/1.0.0`
**Status**: Production Ready
**Purpose**: Liveness checking and latency measurement
**File**: `PingProtocol.cs`

#### **Libp2p.Protocols.RequestResponse** ✅
**Status**: Production Ready
**Purpose**: Generic request-response protocol pattern
**Key Files**:
- `RequestResponseProtocol.cs` - Generic request-response implementation
- `Extension.cs` - Builder extensions
**Tests**: `Libp2p.Protocols.RequestResponse.Tests/`

### Pubsub (Publish-Subscribe)

#### **Libp2p.Protocols.Pubsub** ✅
**Protocol IDs**: `/floodsub/1.0.0`, `/meshsub/1.0.0`, `/meshsub/1.1.0` (🚧), `/meshsub/1.2.0` (🚧)
**Status**: Basic Support (Floodsub ✅, Gossipsub 🚧)
**Purpose**: Publish-subscribe messaging
**Key Files**:
- `PubsubProtocol.cs` - Main pubsub protocol
- `PubsubRouter.cs` - Message routing logic
- `PubsubRouter.Rpc.cs` - RPC handling
- `PubsubRouter.Topics.cs` - Topic management
- `Topic.cs` / `ITopic` - Topic abstractions
- `Dto/Rpc.proto` - Protocol messages
**Tests**: `Libp2p.Protocols.Pubsub.Tests/`, `Libp2p.Protocols.Pubsub.E2eTests/`

### Discovery Protocols

#### **Libp2p.Protocols.MDns** ✅
**Status**: Production Ready (Basic without DNS-SD)
**Purpose**: Local network peer discovery via multicast DNS
**File**: `MDnsDiscoveryProtocol.cs`

#### **Libp2p.Protocols.PubsubPeerDiscovery** ✅
**Status**: Production Ready
**Purpose**: Peer discovery via pubsub
**Files**:
- `PubsubPeerDiscoveryProtocol.cs`
- `PubsubPeerDiscoverySettings.cs`
- `Dto/Peer.proto`
**Tests**: `Libp2p.Protocols.PubsubPeerDiscovery.E2eTests/`

#### **Libp2p.Protocols.KadDht** 🚧 (CURRENT FOCUS)
**Protocol ID**: `/ipfs/kad/1.0.0`
**Status**: Work in Progress - Production Ready Architecture
**Purpose**: Kademlia Distributed Hash Table for content routing and peer discovery

**Directory Structure**:
```
Libp2p.Protocols.KadDht/
├── Dto/
│   └── Kademlia.proto              # Protocol message definitions
├── Kademlia/                       # Core Kademlia algorithm (26 files) ✅
│   ├── IKademlia.cs               # Main DHT interface
│   ├── Kademlia.cs                # Core implementation
│   ├── KBucketTree.cs             # K-bucket routing table
│   ├── IRoutingTable.cs           # Routing table interface
│   ├── ILookupAlgo.cs             # Node lookup algorithms
│   ├── LookupKNearestNeighbour.cs # K-nearest lookup
│   ├── IteratorNodeLookup.cs      # Iterative lookup
│   ├── KBucket.cs                 # Individual K-bucket
│   ├── INodeHealthTracker.cs      # Node health tracking
│   ├── NodeHealthTracker.cs       # Health implementation
│   └── ...                        # Hash utilities, operators, etc.
├── Integration/                    # Bridge layer ✅
│   ├── DhtNode.cs                 # libp2p ↔ Kademlia node bridge
│   ├── DhtKeyOperator.cs          # Key operations
│   ├── DhtNodeHashProvider.cs     # Hash provider
│   ├── DhtMessageSender.cs        # Message sender interface
│   ├── LibP2pKademliaMessageSender.cs # Real network implementation
│   ├── KadDhtIntegrationExtensions.cs # DI extensions
│   └── TypeAdapters.cs            # Type conversion utilities
├── RequestResponse/                # Protocol handlers ✅
│   ├── KadDhtPingProtocol.cs      # /ipfs/kad/1.0.0/ping
│   ├── KadDhtFindNeighboursProtocol.cs # /ipfs/kad/1.0.0/findneighbours
│   ├── KadDhtPutValueProtocol.cs  # /ipfs/kad/1.0.0/putvalue
│   ├── KadDhtGetValueProtocol.cs  # /ipfs/kad/1.0.0/getvalue
│   ├── KadDhtAddProviderProtocol.cs # /ipfs/kad/1.0.0/addprovider
│   └── KadDhtGetProvidersProtocol.cs # /ipfs/kad/1.0.0/getproviders
├── Storage/                        # Data storage ✅
│   ├── IValueStore.cs             # Value storage interface
│   ├── InMemoryValueStore.cs      # In-memory value store
│   ├── IProviderStore.cs          # Provider record interface
│   └── InMemoryProviderStore.cs   # In-memory provider store
├── Session/                        # Session management
│   ├── ISessionManager.cs
│   ├── KademliaSessionManager.cs
│   └── IKademliaNodeAdapter.cs
├── Transport/                      # Network transport
│   ├── IKademliaMessageSender.cs
│   ├── KademliaMessageSender.cs
│   └── KadDhtProtocolExtensions.cs
├── Network/                        # Real libp2p integration
│   └── LibP2pKademliaMessageSender.cs # Production network layer
├── KadDhtProtocol.cs              # Main protocol class ✅
├── KadDhtOptions.cs               # Configuration ✅
└── ServiceCollectionExtensions.cs  # DI registration ✅
```

**Status Summary**:
- ✅ **Core Algorithm**: Complete Kademlia implementation with K-buckets, routing, lookups
- ✅ **Integration Layer**: Full type bridging between libp2p and Kademlia
- ✅ **Protocol Messages**: All 11 protobuf message types defined
- ✅ **Storage Systems**: In-memory value and provider stores with TTL
- ✅ **Network Layer**: Real libp2p network integration
- ✅ **Configuration**: Comprehensive options (Client/Server modes, timeouts, limits)
- 🚧 **Testing**: Comprehensive unit tests, working demo application

**Tests**: `Libp2p.Protocols.KadDht.Tests/`
- `Integration/` - DhtNode, KeyOperator, MessageSender tests
- `Storage/` - Value and provider store tests
- `KadDhtProtocolTests.cs` - Main protocol tests

**Documentation**:
- `INTEGRATION.md` - Integration guide
- `README.md` - Protocol overview
- `.claude/kad_dht_context.md` - Detailed implementation context
- `.claude/kad_dht_session_takeaways.md` - Development session notes

### Circuit Relay

#### **Libp2p.Protocols.Relay** 🚧
**Protocol ID**: `/libp2p/circuit/relay/0.2.0/*`
**Status**: Work in Progress
**Files**:
- `RelayHopProtocol.cs`
- `RelayStopProtocol.cs`

## Sample Applications

### **chat**
**Location**: `src/samples/chat/`
**Purpose**: Simple peer-to-peer chat application
**Key Files**:
- `Program.cs` - Main entry point
- `ChatProtocol.cs` - Custom chat protocol
- `ConsoleReader.cs` - Console I/O handler

### **pubsub-chat**
**Location**: `src/samples/pubsub-chat/`
**Purpose**: Multi-peer chat using pubsub
**Key Files**:
- `Program.cs`
- `ChatService.cs` - Pubsub chat logic
- `ChatMessage.cs` - Message types
- `Gui.cs` - Terminal UI
- `InMemoryLogProvider.cs` - Log capture

### **kad-dht-demo** (CURRENT FOCUS)
**Location**: `src/samples/kad-dht-demo/`
**Purpose**: Comprehensive Kademlia DHT demonstration with real network integration
**Key Files**:
- `Program.cs` - Main demo with real libp2p integration
- `SimpleFileLogger.cs` - File logging utility

**Demo Modes**:
- **Minimal Mode** (default): Clean, production-ready example
- **Detailed Mode**: Comprehensive logging and educational content

**Features**:
- ✅ Real libp2p peer setup with IPv4/IPv6 support
- ✅ Complete DHT operations (put/get/provide/find)
- ✅ Bootstrap with actual libp2p nodes
- ✅ mDNS peer discovery integration
- ✅ Professional logging and error handling

**Documentation**: `README.md` in demo directory

### **transport-interop**
**Location**: `src/samples/transport-interop/`
**Purpose**: Transport protocol interoperability testing
**File**: `Program.cs`

### **perf-benchmarks**
**Location**: `src/samples/perf-benchmarks/`
**Purpose**: Performance benchmarking
**Files**:
- `Program.cs`
- `PerfProtocol.cs`
- `NoStackPeerFactoryBuilder.cs`

## Testing Infrastructure

### Core Tests
- **Libp2p.Core.Tests** - Core functionality tests
- **Libp2p.Core.TestsBase** - Shared test utilities and mocks
- **Libp2p.Core.Benchmarks** - Performance benchmarks

### End-to-End Tests
- **Libp2p.E2eTests** - Full stack E2E tests
- **Libp2p.Protocols.Pubsub.E2eTests** - Pubsub E2E tests
- **Libp2p.Protocols.PubsubPeerDiscovery.E2eTests** - Discovery E2E tests

### Protocol-Specific Tests
Each protocol has its own test project following the pattern `Libp2p.Protocols.[Name].Tests/`

## Documentation

### Main Documentation
**Location**: `docs/`
- `README.md` - Quick start guide
- `logging-tracing.md` - Logging and tracing guide
- `development/` - Advanced development guides
  - `README.md` - Development overview
  - `best-practices.md` - Coding guidelines
  - `transport-layer.md` - Transport layer guide

### .claude Directory (AI Context)
**Location**: `.claude/`
**Purpose**: Claude AI context files for intelligent development assistance

**Files**:
- `project_context.md` - General project architecture and patterns
- `development_commands.md` - Common development workflows and commands
- `kad_dht_context.md` - Kad-DHT specific implementation details
- `kad_dht_session_takeaways.md` - Development session notes
- `project_analysis_2025-09-10.md` - Comprehensive project analysis
- `network_mode_log_analysis.md` - Network mode testing analysis
- `real_network_implementation_summary.md` - Real network layer implementation
- `network_output_analysis_2025-09-10.md` - Network output analysis
- `settings.local.json` - Local settings (permissions)
- `project_index.md` - This file

### Development Notes
**Location**: `devnotes/`
**Purpose**: Development documentation and test projects

**Files**:
- `CLAUDE.md` - Claude AI development guide
- `BESTE.md` - KadDHT demo README
- `LibP2pProgram.cs` - Detailed libp2p demo
- `MinimalProgram.cs` - Minimal demo reference
- `KadDhtTest/` - Standalone test console application

## Key Patterns and Conventions

### Protocol Implementation Pattern
```csharp
// 1. Define protobuf messages in Dto/[Protocol].proto
// 2. Implement main protocol class
public class [Protocol]Protocol : ISessionProtocol
{
    public string Id => "/ipfs/[protocol]/1.0.0";
    public Task DialAsync(IChannel channel, ISessionContext context);
    public Task ListenAsync(IChannel channel, ISessionContext context);
}

// 3. Create builder extensions
public static class [Protocol]ProtocolExtensions
{
    public static IPeerFactoryBuilder Add[Protocol]Protocol(this IPeerFactoryBuilder builder);
}

// 4. Write comprehensive tests
[TestFixture]
public class [Protocol]ProtocolTests { }
```

### Request-Response Pattern
```csharp
public class RequestResponseProtocol<TRequest, TResponse> : ISessionProtocol<TRequest, TResponse>
    where TRequest : IMessage<TRequest>, new()
    where TResponse : IMessage<TResponse>, new()
{
    // Automatic protobuf serialization/deserialization
    // Size-prefixed message handling
    // Type-safe request/response processing
}
```

### Dependency Injection
```csharp
services.AddLibp2p(builder => builder
    .AddProtocol<SomeProtocol>()
    .AddRequestResponseProtocol<TReq, TResp>(protocolId, handler)
)
.AddLogging(/* ... */)
.BuildServiceProvider();
```

## Build and Development

### Prerequisites
- .NET SDK 8.0+ (Currently using .NET 8.0.406)
- Target Framework: .NET 9.0 (configured in Directory.Build.props)
- Git for version control

### Essential Commands
```bash
# Build
dotnet build

# Test
dotnet test

# Run specific sample
dotnet run --project src/samples/kad-dht-demo/

# Clean
dotnet clean

# Restore packages
dotnet restore
```

### Solution Files
- **Libp2p.sln** - Main solution file

## Current Development Status

### Production Ready ✅
- TCP Transport
- Noise Security
- Yamux Multiplexing
- Multistream Protocol Negotiation
- Identify Protocol
- Ping Protocol
- Request-Response Pattern
- Floodsub Pubsub
- mDNS Discovery
- Pubsub Peer Discovery

### Work in Progress 🚧
- **Kademlia DHT** (High Priority - Near Completion)
  - Core algorithm: ✅ Complete
  - Integration layer: ✅ Complete
  - Network layer: ✅ Complete
  - Testing: 🚧 In Progress
- QUIC Transport
- TLS Security
- Gossipsub v1.1/v1.2
- Circuit Relay
- Performance Protocol

### Help Wanted ⬜
- WebTransport
- WebRTC
- Hole Punching

## Recent Updates

**Latest Work (2025-01-20)**:
- Completed comprehensive project indexing
- Updated .claude context documentation
- Validated Kad-DHT implementation status
- Documented all protocol implementations
- Created project structure reference

**Previous Work (2025-09-10)**:
- Real libp2p network layer integration for Kad-DHT
- Comprehensive testing infrastructure
- Working demo application with real network connectivity
- Professional logging and error handling

## Contributing

Contributions are welcome! Please check:
- [Issues](https://github.com/NethermindEth/dotnet-libp2p/issues)
- [CONTRIBUTING.md](./CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](./CODE_OF_CONDUCT.md)

## Community

- **Telegram**: [.NET libp2p](https://t.me/dotnet_libp2p)
- **Discord**: [Channel](https://discord.com/channels/1204447718093750272/1341468555568353330)

## License

MIT License - See [LICENSE](./LICENSE) file

---

*This index provides a comprehensive overview of the dotnet-libp2p project structure, implementation status, and development context. For detailed technical information, refer to the specific documentation files in the .claude directory.*
