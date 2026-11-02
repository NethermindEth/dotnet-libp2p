
```
$ ./Chat
[...]info: Chat[0] Listener started at /ip4/0.0.0.0/tcp/58641/p2p/ABCDEF
```

```
$ ./Chat -d /ip4/127.0.0.1/58641
```

Incoming `/chat/1.0.0` messages may contain up to 1 MiB (1,048,576 bytes) of
UTF-8 payload, including the trailing newline. This application limit is separate
from the 1,024-byte multistream negotiation limit and is shared with the AI server
and MAUI chat samples.
