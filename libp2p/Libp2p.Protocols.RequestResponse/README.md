# RequestResponse Protocol

The RequestResponse Protocol provides a generic, type-safe implementation of request-response pattern in libp2p. It enables peers to exchange structured data using Protocol Buffers serialization with automatic message parsing and error handling.

## Overview

The `RequestResponseProtocol<TRequest, TResponse>` class implements the `ISessionProtocol<TRequest, TResponse>` interface, providing:

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
    .AddRequestResponseProtocol<ExampleRequest, ExampleResponse>(
        protocolId: "/example/1.0.0",
        handler: HandleExampleRequest,
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

### Read deadlines

Existing constructors and builder overloads use a 30-second read timeout. To configure it,
use an overload with `TimeSpan readTimeout` before the protocol ID (or before the
cancellation-aware handler):

```csharp
builder.AddRequestResponseProtocol<ExampleRequest, ExampleResponse>(
    TimeSpan.FromSeconds(15), "/example/1.0.0", HandleExampleRequest);
```

The timeout covers the entire length prefix and protobuf body, including fragmented
reads. The dialer starts it after transmitting the request. The timer is disposed after
deserialization; handlers and response writes have no new deadline. One-way dialing
starts no response timer. Channel closure also cancels reads, and cancellation-aware
handlers retain the channel lifetime token.

Timeouts must be positive, finite, and at most
`TimeSpan.FromMilliseconds(uint.MaxValue - 1)`. Both handler forms accept an optional
`TimeProvider` in the new overloads; builder extensions also resolve it from DI.
Routine channel closure, aborts, duplicate sessions, and expected read cancellations
are logged at Debug. Other stream failures are logged at Warning with the exception
attached. Exceptions continue to propagate to the stream owner.

The `dotnet-libp2p` meter exposes `libp2p.request_response.read_timeouts`, a counter
of failed reads whose deadline expired while channel cancellation was not requested.
It has `protocol` (the registered protocol ID) and `direction` (`inbound` for request
reads, `outbound` for response reads) tags. Ordinary hangups, channel cancellation,
size/parse failures, successful reads, and one-way dialing do not increment it.
Deadline failures remain at Debug; this counter makes them observable without enabling
Debug logging. The existing OpenTelemetry `AddMetrics` integration exports this meter.

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

var response = await session.DialAsync<RequestResponseProtocol<ExampleRequest, ExampleResponse>,
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

### RequestResponseProtocol<TRequest, TResponse>

The core protocol class provides:

- **Constructor Parameters**:
  - `protocolId`: Unique identifier for the protocol (e.g., "/my-app/1.0.0")
  - `handler`: Async function to process requests and generate responses
  - `loggerFactory`: Optional logger factory for debugging
  - `expectsResponse`: Optional predicate for protocols that mix requests and one-way messages. Defaults to expecting a response.

- **Key Methods**:
  - `ListenAsync()`: Handles incoming requests from remote peers
  - `DialAsync()`: Sends requests to remote peers and waits for responses

When `expectsResponse` returns `false`, `DialAsync()` completes after a successful write and returns an empty `TResponse`; this is not a remote acknowledgement. `ListenAsync()` still invokes the handler but does not send its response.

### RequestResponseExtensions

The extension class provides a convenient builder method:

```csharp
public static IPeerFactoryBuilder AddRequestResponseProtocol<TRequest, TResponse>(
    this IPeerFactoryBuilder builder,
    string protocolId,
    Func<TRequest, ISessionContext, Task<TResponse>> handler,
    bool isExposed = true,
    Func<TRequest, bool>? expectsResponse = null)
```

**Parameters**:
- `builder`: The peer factory builder to extend
- `protocolId`: Unique protocol identifier
- `handler`: Function to handle incoming requests
- `isExposed`: Whether the protocol should be advertised to other peers
- `expectsResponse`: Whether each request expects a wire response

## Type Constraints

Both `TRequest` and `TResponse` must satisfy:

```csharp
where TRequest : class, IMessage<TRequest>, new()
where TResponse : class, IMessage<TResponse>, new()
```
