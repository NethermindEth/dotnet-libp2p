# Best practices for implementing protocols

## Package exchange

Protocols can perform a handshake and exchange payloads using any data format. Messages are usually encoded as:

- simple text
- protobuf
- raw binary streams

Regardless of data format or protocol level, a protocol usually needs to know the payload length before reading from the channel. Prefixing the payload with a length is the typical pattern. Use varint when the length is dynamic.

Varint is a network-friendly encoding for positive integers up to 9 bytes in length. Check the `VarInt` implementation in the Core project.

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
