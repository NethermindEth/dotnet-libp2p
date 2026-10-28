<!-- Draft for host publication; this file does not update the live pull request. -->

## Summary

Bound inbound TCP connections, upgrades, session initialization and built-in protocol streams.
Admission defaults to 256 connections per local peer and 32 per IPv4 address or native IPv6 /64.
Excess sockets are refused before protocol dispatch or receive-buffer rental. Under admission
pressure, established sessions without application operations for 60 seconds may be disconnected;
the interval is configurable. Queued calls, open streams and their cleanup protect a session.
New arrivals retry after teardown releases admission; workers are never abandoned to refund a permit.

Inbound upgrades and session initialization each have a 15-second deadline. At most 128 inbound
session initializations may run per local peer; outbound initializations use no inbound allowance
and retain their deadline. Cancellation aborts
actual I/O, joins owned workers and releases peer-store retention. Built-in protocols default to
512 inbound streams total and 64 per remote peer, with pubsub versions sharing an allowance.
Receive pumps retain PR #406's single scratch rental, owned copies and exactly-once return.
Orderly EOF remains distinct from cancellation, reset and truncated Noise messages.
TCP connection activities export `connected {endpoint}` without accumulating events on the listener.

Custom initializers must cooperate with `session.Closed`: an unfinished inbound initializer can
retain its bounded initialization allowance after its peer-store pin is released. TCP admission likewise
remains charged until protocol teardown finishes. A custom upgrade that ignores its connection
token can hold an admission slot and prevent `ListenAsync` from returning until it finishes.
Active sessions and noncooperative work can still saturate admission. Optional transports share
core lifecycle protections; their native admission and pre-context handshakes remain outside TCP's cap.

## Validation

- Release solution build with warnings treated as errors: zero warnings and errors.
- Twelve affected and dependent test projects run for the initialization fix: 1,346 passed, five existing skips. Core: 566; TCP: 36; Multistream: 21; Noise: 14; TLS: 26; Yamux: 299; E2E: 225. QUIC, WebSocket, WebRTC, Plaintext and Mplex suites also passed. Tests use `dotnet test --project libp2p/<project>/<project>.csproj -c Release --no-build -p:ArtifactsPath="$TMPDIR/art" -- --minimum-expected-tests 1 --timeout 180s`.
- Four initialization regression cases failed with the shared budget at the retained head and passed after the fix. They cover both arrival orders, outbound success and expiry, inbound saturation and allowance recovery.
- New coverage includes native IPv6 prefix sharing and recovery, scoped link-local addresses, pressure-driven idle eviction, active streams and queued calls, synchronous failure cleanup, per-connection activity events, and real registered TCP recovery on IPv4 and IPv6 with an injected clock.
- Earlier retained-candidate validation: reversible mutations removing prefix grouping, idle trimming or the connection event each failed their corresponding regression; mutations were restored before final builds and tests. Those mutations were not repeated for the initialization fix.
- Earlier TCP Debug build and tests also passed (36 tests), including cancellation-registration and disconnection-delegate cleanup; Debug was not repeated for the initialization fix.
- Repository whitespace and spelling checks passed. No tests or CI thresholds were weakened.

The five skips cover Windows-specific CNG persistence and four explicit WebRTC smoke tests.
Windows QUIC/TLS CI and external-client interoperability have not been run locally.

Closes #369
Closes #374
Closes #378
Closes #400
Closes #403
