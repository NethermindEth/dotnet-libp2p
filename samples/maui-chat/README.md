
# Prerequisites

Development requires MS Visual Studio `.NET Multi-platform UI development` workload installed.
It was tested using x86_x64 Android Emulator, but probably would work on other Android platforms.

This sample was not developed or tested on iOS. Contributions that add and verify iOS support are welcome.

In case of issues with starting it up, workload restore may be needed:

```
dotnet workload restore
```

Incoming `/chat/1.0.0` messages may contain up to 1 MiB (1,048,576 bytes) of
UTF-8 payload, including the trailing newline. This matches the console chat
and AI server samples and is separate from the 1,024-byte multistream negotiation limit.
