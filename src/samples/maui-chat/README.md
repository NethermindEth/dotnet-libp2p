
# Prerequisites

Development requires .NET Multi-platform UI development workload installed. it was tested using x86_x64 Android Emulator, but probably would work on other platforms.

In case ofissues with starting up the app, workload restore may be needed:

```
dotnet workload restore
```

## TODOs

 - [ ] Test TCP on other platforms
 - [ ] Implement TCP support on iOS, ny adding libsodium
 - [ ] Implement msquic support, by adding msquic and openssl libs
