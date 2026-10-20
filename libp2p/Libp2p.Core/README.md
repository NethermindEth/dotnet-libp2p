# Core routines and API

`PeerStore` retains at most 10,000 entries by default; its constructor accepts a
different capacity. Active sessions retain their entries until disconnect,
including failed initialization. A new session cannot be admitted when all slots
belong to active sessions or configured peers. Local seeds take precedence over
remote admissions, including authenticated sessions. Connecting or publishing
metadata does not turn a remote peer into a configured peer; disconnected remote
entries are replaced before configured entries, in insertion order.

`Discover(addresses)` adds deliberately configured peers. Network discovery
sources must use `Discover(addresses, isTrusted: false)`; signed peer-exchange
records already use this remote policy. Unsigned remote discovery can refresh
remote addresses after a connection, but cannot overwrite configured addresses or
verified signed records. Only explicit local seeding can replace a disconnected
configured entry. Entries returned by `GetPeerInfo` can be evicted when disconnected;
use `SetSupportedProtocols` and `SetAgentVersion` to publish metadata to the current entry.
