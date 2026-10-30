# Manual interop for WebRTC Direct

CI cannot test this transport against another implementation. There is no browser, no go-libp2p
peer and no js-libp2p peer on the runner, and a self-dial is structurally incapable of catching a
bug both halves make together — the inverted Noise role this transport shipped with for a while was
exactly that, and no test saw it. This file is the procedure that covers what CI cannot, to be run
by hand before the pull request, with the output pasted into the PR body.

Nothing here is a substitute for the automated suite, and the automated suite is not a substitute
for this.

## What only a real peer can settle

| Question | Why CI cannot answer it |
|---|---|
| Does a real client's DTLS `ClientHello` offer `use_srtp`? | `DtlsSrtpServer` **requires** it: against a client that does not offer it the handshake fails with `internal_error(80)`, and there is no middle setting. Every WebRTC stack is believed to offer it even for data-channel-only connections, but that is an assumption, not a measurement. This is the first thing to check. |
| Does the v1 flow work end to end? | Our dialer is v2-only, so a loopback can never drive v1. Only `WebRtcIceCredentials` unit tests touch it; the rest is written from the spec text. |
| Do real browser ufrags parse? | The listener splits the STUN `USERNAME` on its first colon and keys MESSAGE-INTEGRITY with the server ufrag. Our own ufrags are the only ones CI ever sees. |
| Does the listener survive the real consent-check cadence? | A real dialer keeps sending binding requests for the life of the connection. CI covers one request after admission; the real period is seconds and the real pair timeout is tens of seconds. |
| Is `a=max-message-size:16384` actually honoured? | We never negotiate it — there is no SDP on the listen path — and enforce it unilaterally through `WebRtcFraming.MaxFrameSize`. |

## The listener

`samples/transport-interop` already speaks `webrtc-direct` and the libp2p test-plans Redis
protocol, so it is the listener for every case below rather than a throwaway program.

```sh
docker run -d --name redis-interop -p 6379:6379 redis:alpine

cd samples/transport-interop
TRANSPORT=webrtc-direct \
IS_DIALER=false \
TEST_KEY=webrtc \
REDIS_ADDR=127.0.0.1:6379 \
LISTENER_IP=<this machine's LAN IP> \
TEST_TIMEOUT_SECS=300 \
dotnet run
```

`webrtc-direct` is one of the sample's stackless transports, so `MUXER` and `SECURE_CHANNEL` are
not set — the transport brings its own. Set `LISTENER_IP` explicitly: the sample auto-detects an
interface, and a listener that picks the wrong one is reachable from nothing. Loopback is fine for
the go and js cases on the same machine; a browser on another device needs the LAN address.

It prints the address it published and pushes it to Redis under
`<TEST_KEY>_listener_multiaddr`. **Capture that line** — the `/certhash` in it is what every dialer
below authenticates against:

```
Listening on /ip4/<ip>/udp/<port>/webrtc-direct/certhash/<base64url>/p2p/<peer id>
```

## The dialers

Run each against the same Redis. Take image names and versions from
[libp2p/test-plans](https://github.com/libp2p/test-plans) `transport-interop` rather than from
here: the set of images and which of them speak `webrtc-direct` changes, and a stale tag in this
file would be worse than no tag. **Record the exact image and version you used** — a result
without it cannot be reproduced or compared against the next run.

Each dialer needs `transport=webrtc-direct`, `is_dialer=true`, the same `TEST_KEY`, and the same
`REDIS_ADDR`. On success a test-plans dialer prints a JSON line with the handshake and ping
timings; that line is the result.

1. **go-libp2p dialer.** The one that matters most, because go's `udpmux` is what the listener's
   design was measured against. Check its `webrtc-direct` support before assuming it.
2. **js-libp2p dialer (Node).** Second implementation, different DTLS and SCTP stacks.
3. **A browser.** The only source of real browser ufrags and real consent-check cadence, and the
   case this transport exists for. A minimal page using `@libp2p/webrtc`'s `webRTCDirect()`
   transport dialling the multiaddr above is enough; it must be served over HTTPS or from
   `localhost`, since a secure context is required. Record the browser and version.

For the **v1 flow**, point a dialer that still speaks v1 at the same address: the listener accepts
both prefixes and tells them apart from the `USERNAME` alone. If no available implementation speaks
v1 any more, say so in the PR body rather than leaving it unstated — v1 then ships uncovered by
anything but unit tests, and a reviewer should know.

## What to capture

For every case, in the PR body:

- the implementation, image and version, and the browser and version where that applies;
- our listener's published multiaddr;
- the dialer's result line, or its error;
- our listener's log for the connection — at `Debug` it names the session reaching `Established`,
  the DTLS fingerprint it bound, and each stream it carries;
- whether `use_srtp` appeared in the client's `ClientHello`. A `tshark -f 'udp port <port>' -Y dtls`
  capture is the cheapest way to see it, and the same capture shows the binding requests arriving
  for the life of the connection;
- for a connection you keep open past a minute: that it is still alive, and that the listener is
  still answering consent checks rather than having stopped at admission.

A failure is a result too. Paste it with the same detail; "did not work" is not a finding anyone
can act on.

## Template for the PR body

```
### Manual interop

Ran by hand on <date>, commit <sha>, against <OS/kernel>.
Listener: samples/transport-interop, TRANSPORT=webrtc-direct, listening on
  /ip4/.../udp/.../webrtc-direct/certhash/.../p2p/...

| Peer | Version | Result | use_srtp offered | Notes |
|---|---|---|---|---|
| go-libp2p  | ... | ... | ... | ... |
| js-libp2p  | ... | ... | ... | ... |
| browser    | ... | ... | ... | ... |
| v1 dialer  | ... | ... | ... | ... |

<the dialer result lines and any errors, verbatim>

CI does not cover any of the above: no browser, no go-libp2p peer and no js-libp2p peer on the
runner. The automated tests that carry an oracle from outside this codebase are the 32-byte Noise
message-one assertion, the captured STUN fixtures from SIPSorcery's own ICE agent, the spec's
published prologue vector, and the absolute stream-id literals.
```
