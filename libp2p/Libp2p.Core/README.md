# Core routines and API

`IReader.ReadLineAsync()` limits the declared payload to 1,024 bytes, including
any trailing newline and excluding the varint length prefix. Callers that need
larger lines must use `ReadLineAsync(maxLength, token)`. Oversized declarations
throw `InvalidDataException` before reading the payload; integer length prefixes
above `int.MaxValue` or longer than five bytes throw `FormatException`.

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

Address lists use their first identified peer as the target; entries without that
identity are excluded from storage, announcements and dialing. Dialing by peer ID
always uses the requested identity, even if an older or directly modified cache
entry contains other addresses. An entirely unidentified list is rejected explicitly.

DNS budgets limit lookups and derived addresses. Caller literals remain candidates,
with at most 16 transport attempts active at once under one connection deadline.
Failed attempts free a slot immediately; later fallbacks are not truncated. Cancellation
or success cancels losing attempts without starting the remaining candidates.
