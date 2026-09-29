# QUIC transport

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/quic)

## Native dependencies

### Windows

Windows 11 and Windows Server 2022 or later include the MsQuic implementation used by .NET. The bundled Schannel backend supports this transport, including libp2p's mutual certificate authentication. No app-local MsQuic DLL is required.

### Linux

`libmsquic.so` is required, as described [here](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Quic/readme.md#linux).

Installation:

```sh
apt install libmsquic
```
