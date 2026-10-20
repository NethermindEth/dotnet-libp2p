# Core routines and API

`PeerStore` retains at most 10,000 entries by default; its constructor accepts a
different capacity. Active sessions retain their entries until disconnect,
including failed initialization. A new session cannot be admitted when all slots
belong to active sessions. Local seeds and authenticated metadata take precedence
over remote discoveries; disconnected entries within each group are replaced in
insertion order.

`Discover(addresses)` adds deliberately configured peers. Network discovery
sources must use `Discover(addresses, isTrusted: false)`; signed peer-exchange
records already use this remote policy. Remote discovery cannot evict or overwrite
trusted entries. Entries returned by `GetPeerInfo` can be evicted when disconnected;
use `SetSupportedProtocols` to publish metadata to the current entry.
