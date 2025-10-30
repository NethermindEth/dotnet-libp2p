# Development Commands & Workflows

## Build Commands

### Solution Level
```bash
# Full solution build
dotnet build

# Clean all projects
dotnet clean

# Restore all packages
dotnet restore

# Build in Release mode
dotnet build -c Release
```

### Project Level
```bash
# Build kad-dht protocol
dotnet build src/libp2p/Libp2p.Protocols.KadDht/

# Build core library
dotnet build src/libp2p/Libp2p.Core/

# Build main library
dotnet build src/libp2p/Libp2p/
```

## Test Commands

### All Tests
```bash
# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity detailed

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Specific Test Projects
```bash
# Kad-DHT tests only
dotnet test src/libp2p/Libp2p.Protocols.KadDht.Tests/

# Core tests only  
dotnet test src/libp2p/Libp2p.Core.Tests/

# Protocol tests pattern
dotnet test --filter "FullyQualifiedName~KadDht"
```

### Test Filtering
```bash
# Run specific test method
dotnet test --filter "Method=PutValueAsync_ShouldStoreValueLocally"

# Run tests by category
dotnet test --filter "Category=Integration"

# Run tests by namespace
dotnet test --filter "FullyQualifiedName~Libp2p.Protocols.KadDht"
```

## Development Workflow

### New Feature Development
```bash
# 1. Create feature branch
git checkout -b feature/kad-dht-value-storage

# 2. Build and test current state
dotnet build
dotnet test src/libp2p/Libp2p.Protocols.KadDht.Tests/

# 3. Implement changes...

# 4. Run tests frequently during development
dotnet test src/libp2p/Libp2p.Protocols.KadDht.Tests/ --filter "Method=PutValueAsync*"

# 5. Full validation before commit
dotnet build
dotnet test
```

### Protocol Development Workflow
```bash
# 1. Update protobuf definitions
# Edit: src/libp2p/Libp2p.Protocols.KadDht/Dto/Kademlia.proto

# 2. Regenerate protobuf code
dotnet build src/libp2p/Libp2p.Protocols.KadDht/

# 3. Implement protocol logic
# Edit: src/libp2p/Libp2p.Protocols.KadDht/KadDhtProtocol.cs

# 4. Write/update tests
# Edit: src/libp2p/Libp2p.Protocols.KadDht.Tests/KadDhtProtocolTests.cs

# 5. Test specific functionality
dotnet test src/libp2p/Libp2p.Protocols.KadDht.Tests/
```

## Debugging Commands

### Verbose Logging
```bash
# Run with detailed logging
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project samples/kad-dht-demo/ --verbosity diagnostic

# Or set log level for specific namespace
export Logging__LogLevel__Nethermind.Libp2p.Protocols.KadDht=Debug
```

### Watch Mode Development
```bash
# Auto-rebuild on file changes
dotnet watch build src/libp2p/Libp2p.Protocols.KadDht/

# Auto-test on file changes
dotnet watch test src/libp2p/Libp2p.Protocols.KadDht.Tests/
```

## Code Generation

### Protobuf Generation
```bash
# Manual protobuf generation (usually automatic via build)
dotnet build src/libp2p/Libp2p.Protocols.KadDht/

# Check generated files
ls src/libp2p/Libp2p.Protocols.KadDht/obj/Debug/net9.0/
```

### Clean Generated Code
```bash
# Clean generated files
dotnet clean src/libp2p/Libp2p.Protocols.KadDht/
rm -rf src/libp2p/Libp2p.Protocols.KadDht/obj/
rm -rf src/libp2p/Libp2p.Protocols.KadDht/bin/
```

## Package Management

### Add Dependencies
```bash
# Add package to specific project
dotnet add src/libp2p/Libp2p.Protocols.KadDht/ package Microsoft.Extensions.Caching.Memory

# Add project reference
dotnet add src/libp2p/Libp2p.Protocols.KadDht/ reference src/libp2p/Libp2p.Core/
```

### Update Dependencies
```bash
# Update all packages
dotnet list package --outdated
dotnet update

# Update specific package
dotnet add src/libp2p/Libp2p.Protocols.KadDht/ package Google.Protobuf
```

## Git Workflow

### Feature Development
```bash
# Start feature
git checkout -b feature/implement-dht-values
git push -u origin feature/implement-dht-values

# Development cycle
git add .
git commit -m "Add PUT_VALUE protocol message definitions"
git push

# Final integration  
git checkout main
git pull origin main
git merge feature/implement-dht-values
```

### Quick Commits
```bash
# Stage specific files
git add src/libp2p/Libp2p.Protocols.KadDht/KadDhtProtocol.cs
git add src/libp2p/Libp2p.Protocols.KadDht.Tests/KadDhtProtocolTests.cs

# Commit with descriptive message
git commit -m "Implement KadDhtProtocol class with value storage operations

- Add KadDhtProtocol implementing ISessionProtocol
- Support PUT_VALUE and GET_VALUE operations  
- Add in-memory value storage with TTL
- All existing tests now pass"
```

## Performance & Profiling

### Memory Analysis
```bash
# Run with memory profiling (requires dotnet-dump tool)
dotnet-counters monitor --process-id $(pgrep -f "Libp2p") --counters System.Runtime

# Generate memory dump
dotnet-dump collect -p $(pgrep -f "Libp2p")
```

### Performance Testing
```bash
# Build optimized version
dotnet build -c Release

# Run performance-focused tests
dotnet test -c Release --filter "Category=Performance"

# Profile specific operations
dotnet run -c Release --project samples/kad-dht-demo/ -- --benchmark-mode
```

## Documentation Generation

### XML Documentation
```bash
# Generate XML docs (configured in .csproj)
dotnet build -c Release

# Find generated documentation
find . -name "*.xml" -path "*/bin/Release/*"
```

## Troubleshooting Commands

### Common Issues
```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Reset to clean state
git clean -fdx
dotnet restore
dotnet build

# Check project dependencies
dotnet list src/libp2p/Libp2p.Protocols.KadDht/ reference
dotnet list src/libp2p/Libp2p.Protocols.KadDht/ package
```

### Build Issues
```bash
# Verbose build output
dotnet build --verbosity diagnostic

# Build specific target framework
dotnet build --framework net9.0

# Force rebuild
dotnet build --no-incremental
```

## CI/CD Integration

### Local CI Simulation
```bash
# Simulate CI build
export CI=true
dotnet restore --locked-mode
dotnet build -c Release
dotnet test -c Release --no-build
```

### Packaging
```bash
# Create NuGet packages
dotnet pack -c Release

# Find generated packages
find . -name "*.nupkg" -path "*/bin/Release/*"
```

These commands provide the essential toolkit for efficient .NET libp2p development, especially focusing on the Kad-DHT implementation workflow.