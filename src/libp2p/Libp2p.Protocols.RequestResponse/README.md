# RequestResponse Protocol

The RequestResponse Protocol provides a generic, type-safe implementation of request-response pattern in libp2p. It enables peers to exchange structured data using Protocol Buffers serialization with automatic message parsing and error handling.

## Overview

The `GenericRequestResponseProtocol<TRequest, TResponse>` class implements the `ISessionProtocol<TRequest, TResponse>` interface, providing:

- **Type Safety**: Strongly typed request and response messages
- **Protocol Buffers**: Automatic serialization/deserialization using Google.Protobuf
- **Error Handling**: Comprehensive logging and exception management
- **Bidirectional Communication**: Support for both client (dial) and server (listen) operations

## Usage

### Register the Protocol

Use the extension method to register your protocol with the peer factory:

```csharp
using Nethermind.Libp2p.Protocols;

// Register the protocol with a handler function
var peerFactory = new PeerFactoryBuilder()
    .AddGenericRequestResponseProtocol<ExampleRequest, ExampleResponse>(
        protocolId: "/example/1.0.0",
        handler: HandleExampleRequest,
        loggerFactory: loggerFactory,
        isExposed: true)
    .Build();

// Handler function implementation
private static async Task<ExampleResponse> HandleExampleRequest(
    ExampleRequest request,
    ISessionContext context)
{
    // Process the request
    var results = ProcessQuery(request.Query, request.Limit);

    return new ExampleResponse
    {
        Results = { results },
        Success = true
    };
}
```

### Using the Protocol

#### As a Client (Dialing)

```csharp
// Connect to a remote peer
ISession session = await localPeer.DialAsync(remoteAddress);

// Send a request and receive a response
var request = new ExampleRequest
{
    Query = "search term",
    Limit = 10
};

var response = await session.DialAsync<GenericRequestResponseProtocol<ExampleRequest, ExampleResponse>,
                                      ExampleRequest,
                                      ExampleResponse>(request);

Console.WriteLine($"Success: {response.Success}");
Console.WriteLine($"Results: {string.Join(", ", response.Results)}");
```

#### As a Server (Listening)

The server-side handling is automatic when you register the protocol with a handler function. The protocol will:

1. Listen for incoming connections
2. Deserialize the request message
3. Call your handler function
4. Serialize and send the response
5. Close the connection

## Protocol Implementation Details

### GenericRequestResponseProtocol<TRequest, TResponse>

The core protocol class provides:

- **Constructor Parameters**:
  - `protocolId`: Unique identifier for the protocol (e.g., "/myapp/1.0.0")
  - `handler`: Async function to process requests and generate responses
  - `loggerFactory`: Optional logger factory for debugging

- **Key Methods**:
  - `ListenAsync()`: Handles incoming requests from remote peers
  - `DialAsync()`: Sends requests to remote peers and waits for responses

### RequestResponseExtensions

The extension class provides a convenient builder method:

```csharp
public static IPeerFactoryBuilder AddGenericRequestResponseProtocol<TRequest, TResponse>(
    this IPeerFactoryBuilder builder,
    string protocolId,
    Func<TRequest, ISessionContext, Task<TResponse>> handler,
    ILoggerFactory? loggerFactory = null,
    bool isExposed = true)
```

**Parameters**:
- `builder`: The peer factory builder to extend
- `protocolId`: Unique protocol identifier
- `handler`: Function to handle incoming requests
- `loggerFactory`: Optional logging factory
- `isExposed`: Whether the protocol should be advertised to other peers

## Type Constraints

Both `TRequest` and `TResponse` must satisfy:

```csharp
where TRequest : class, IMessage<TRequest>, new()
where TResponse : class, IMessage<TResponse>, new()
```
