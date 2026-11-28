# Best practices for implementing protocols

## Package exchange

Protocols can perform a handshake and exchange payloads using any data format. Messages are usually encoded as:

- simple text
- protobuf
- raw binary streams

Regardless of data format or protocol level, a protocol usually needs to know the payload length before reading from the channel. Prefixing the payload with a length is the typical pattern. Use varint when the length is dynamic.

Varint is a network-friendly encoding for positive integers up to 9 bytes in length. Check the `VarInt` implementation in the Core project.

## Reading and forwarding channel data

For length-prefixed text or protobuf messages, use `ReadLineAsync(maxLength, token)` or
`ReadPrefixedProtobufAsync(parser, maxMessageLength, token)`. Both enforce the protocol's
size limit before reading the payload and release pooled storage once parsing finishes.

For a binary frame, use `ReadOwnedAsync` with a `using` declaration. Call `OrThrow()`
on the awaited result to check the read status and access its bytes within that scope.
Use `ReadAsync` when the bytes themselves must outlive the read. It returns a stable
sequence and copies pooled data when needed.

```csharp
using ReadResult frame = await channel.ReadOwnedAsync(frameLength, token: token);
ReadOnlySequence<byte> payload = frame.OrThrow();
// Parse payload before leaving this scope.
```

To forward chunks, pass each read to the next writer. This works for pooled buffers,
segmented writes, and custom channels. The iterator disposes each read when it advances
or exits. The destination receives its own reference to pooled bytes if it supports
them, or a stable copy otherwise.

```csharp
await foreach (ReadResult chunk in source.ReadAllOwnedAsync(token))
{
    await chunk.WriteToAsync(destination, token).OrThrow();
}
```

The loop ends on clean input EOF. The protocol decides whether to half-close the destination
with `WriteEofAsync`. See [transport and connection protocols](./transport-layer.md) for
socket and stream receive loops.

## Using protobuf

There is a protobuf generator that can generate C# types in a unified manner. To integrate it:

- Add `Libp2p.Generators.Protobuf` as an analyzer to the project:

  ```xml
      <ProjectReference Include="..\Libp2p.Generators.Protobuf\Libp2p.Generators.Protobuf.csproj"
          OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  ```

- Add `.proto` files for the structures needed and set "Build action" = "C# analyzer additional file" for each;
- Add `option csharp_namespace = "<namespace>";` if the classes should belong in a particular namespace;
- Build the project.

## Testing

We use `NUnit` and `NSubstitute` to test, with some additional packages:

```xml
    <PackageReference Include="NSubstitute" />
    <PackageReference Include="NSubstitute.Analyzers.CSharp" />
    <PackageReference Include="NUnit" />
    <PackageReference Include="NUnit3TestAdapter" />
    <PackageReference Include="NUnit.Analyzers" />
```

Test examples can be found in the `Libp2p.Protocols.Multistream.Tests` project.

You can also use the `Libp2p.Core.TestsBase` project, which contains `TestChannel` and shared test helpers. Add reusable testing helpers there when they are useful across projects.
