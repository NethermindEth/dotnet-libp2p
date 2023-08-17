# Best practices implementing protocols

## Package exchange

Protocol can do handshake and exchange real payload, using any kind of data format. The messages are usually encoded via:
- simple text;
- protobuf;
- or go as just a binary data stream.

Regardless of data format and protocol level, the protocol should know about data length while reading it from the channel.
So this is typical to send data length before the payload. You can use varint for that.

Varint is a network friendly encoding of dynamic length positive integer up to 9 bytes in length. Check `VarInt` implementation in the Core project.

## Using protobuf

There is a protobuf generator, that can be used to easily generate C# types in a unified manner. To integrate it, you need to:
- Add `Libp2p.Generators.ProtobufGenerator` as analyzer to the project:
  ```xml
      <ProjectReference Include="..\Libp2p.Generators.ProtobufGenerator\Libp2p.Generators.ProtobufGenerator.csproj" 
          OutputItemType="Analyzer" ReferenceOutputAssembly="false"/>
  ```
- Create `Dto` directory in the project;
- Add `.proto` files for the structures needed;
- Add `option csharp_namespace = "<namespace>";` if the classes should belong in a particular namespace;
- Build the project.

## Testing 

We use `NUnit` and `NSubstitute` to test, with some additional packages
```xml
    <PackageReference Include="NSubstitute" Version="5.0.0"/>
    <PackageReference Include="NSubstitute.Analyzers.CSharp" Version="1.0.16"/>
    <PackageReference Include="NUnit" Version="3.13.3"/>
    <PackageReference Include="NUnit3TestAdapter" Version="4.3.0"/>
    <PackageReference Include="NUnit.Analyzers" Version="3.5.0"/>
```

Tests examples can be found in `Libp2p.Protocols.Multistream.Tests` project.

You can use `Libp2p.Core.TestsBase` project additionally, which contains `TestChannel` and other stuff. Feel free to add testing helpers to it that you find useful!