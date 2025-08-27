
# Prerequisites

Development requires MS Visual Studio `.NET Multi-platform UI development` workload installed.
It was tested using x86_x64 Android Emulator, but probably would work on other Android platforms.

In case of issues with starting it up, workload restore may be needed:

```
dotnet workload restore
```

## TODOs

 - [ ] Test TCP on other platforms
 - [ ] Implement TCP support on iOS, ny adding libsodium
 - [ ] Implement msquic support, by adding msquic and openssl libs
