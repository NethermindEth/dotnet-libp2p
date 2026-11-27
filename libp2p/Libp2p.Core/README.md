# Core routines and API

`IsLoopbackAddress`, `IsUnspecified`, `IsLinkLocal` and `IsPrivateAddress` allow
callers to select the address ranges they need. Private addresses here include
IPv4 private-use, IPv6 unique-local and deprecated IPv6 site-local ranges.
IPv4-mapped IPv6 uses the embedded IPv4 address for classification.

`IsPublicAddressCandidate` checks whether a literal IP is suitable for
consideration as a public peer endpoint. It excludes multicast, scoped IPv6 and
IANA special-purpose ranges, including globally reachable service reservations.
Native IPv6 candidates must fall within 2000::/3. Assignment, transport support
and reachability require separate checks.
[IPv4 special-purpose registry](https://www.iana.org/assignments/iana-ipv4-special-registry),
[IPv6 special-purpose registry](https://www.iana.org/assignments/iana-ipv6-special-registry),
[IPv6 global unicast registry](https://www.iana.org/assignments/ipv6-unicast-address-assignments).

The `Multiaddress` overloads inspect a single leading literal IP and leave the
multiaddress intact. DNS names, relay paths, non-IP endpoints and ambiguous
addresses fail all five predicates. `GetDirectIPAddress` exposes the same
literal with its original family and scope. These helpers perform no DNS lookups.

`IReader.ReadLineAsync()` limits the declared payload to 1,024 bytes, including
any trailing newline and excluding the varint length prefix. Callers that need
larger lines must use `ReadLineAsync(maxLength, token)`. Oversized declarations
throw `InvalidDataException` before reading the payload; integer length prefixes
above `int.MaxValue` or longer than five bytes throw `FormatException`.
The `/chat/1.0.0` samples select a larger application limit of 1 MiB explicitly
for incoming messages, including the newline, to accommodate long prompts and replies.

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
with at most 16 transport attempts active at once under one connection deadline
that starts after resolution and includes session initialization. Configure it with
`PeerResourceLimits.ConnectionTimeout`, which defaults to 15 seconds.
Failed attempts free a slot immediately; later fallbacks are not truncated. Cancellation
or success cancels losing attempts without starting the remaining candidates.
