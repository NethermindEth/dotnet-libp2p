# Circuit Relay v2

Implements the [Circuit Relay v2](https://github.com/libp2p/specs/blob/master/relay/circuit-v2.md) protocol:

- **Hop** (`/libp2p/circuit/relay/0.2.0/hop`): reservation of relay slots and connection initiation through the relay.
- **Stop** (`/libp2p/circuit/relay/0.2.0/stop`): connection termination between the relay and the target peer.

Enable relay in the stack with `WithRelay()` on the peer factory builder. The reservation store is registered automatically when using `AddLibp2p()`.

**Critical gap:** Stream bridging is required for a working relayed connection. Today the relay completes the Hop and Stop handshakes and returns STATUS OK but does not tie the two streams together. Implementing this will require either a way to obtain the stop stream/channel after `DialAsync` or lower-level support to bridge two streams by ID.
