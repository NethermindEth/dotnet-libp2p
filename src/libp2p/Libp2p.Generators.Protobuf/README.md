# Protobuf generator

The project  automatically generates C# types for protobuf types using protoc(no need to install it separately, it's in the package).

## Usage

1. Add [Google.Protobuf](https://www.nuget.org/packages/Google.Protobuf) package
1. Add [Libp2p.Generators.Protobuf](https://www.nuget.org/packages/Nethermind.Libp2p.Generators.Protobuf) as analyzer dependency to you project
   ```xml
   ...
   <ItemGroup>
     ...
     <PackageReference Include="Libp2p.Generators.Protobuf" Version="3.25.1" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
   </ItemGroup>
   ...
   ```
1. Make sure that Google.Protobuf version matches Libp2p.Generators.Protobuf project version
1. Add a .proto file and set "Build Action" to "C# analyzer additional file" for it, "Copy to Output Directory"="Do not copy". It should look like
  `<AdditionalFiles Include="Dto\Rpc.proto" CopyToOutputDirectory="Never" />` in csproj file.
1. .cs file should be generated next to .proto one.

## Additional notes

In case you want to use protobuf version that is more modern that is used by the generator, feel free to create an issue with request to update the version or just make a pr.
