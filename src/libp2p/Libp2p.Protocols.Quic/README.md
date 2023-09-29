# QUIC transport

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/quic)

## Native dependencies

### Windows

Quic support is fully inbuilt in the runtime. By default it utilizes `Schannel`-based implementation which is not compatible with libp2p, `OpenSSL`-based version is required. To use it instead of standard one, a runtime library needs to be replaced:

1. Locate the current NETCore runtime: `dotnet --info`
2. Find `msquic.dll` for the current version and replace it with one from the project.

Typical location of `msquic.dll` is "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\x.y.zzz\msquic.dll"

### Linux

`libmsquic.so` is required , as described [here](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Quic/readme.md#linux).
