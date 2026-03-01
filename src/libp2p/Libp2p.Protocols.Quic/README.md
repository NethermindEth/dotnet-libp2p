# QUIC transport

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/quic)

## Native dependencies

### Windows

Quic support is fully inbuilt in the runtime. By default it utilizes `Schannel`-based implementation which is not compatible with libp2p, `OpenSSL`-based version is required.
To use it instead of standard one, a runtime library needs to be replaced or the application should be published as self contained.

In the future OpenSSL version will be used automatically with a need in workarounds. Requires [Support overriding MsQuic.dll in the application directory on Windows](https://github.com/dotnet/runtime/commit/4b16dce6097aa70e09bb82e27c5055421cf74ded) feature to get into a release.

### Linux

`libmsquic.so` is required, as described [here](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Quic/readme.md#linux).

Installation:

```sh
apt install libmsquic
```
