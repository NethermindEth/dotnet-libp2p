# Yamux protocol

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/yamux)

By default, a locally closed stream waits for its outbound data to drain or for the connection to close. Applications that need to bound retained stream state can pass a positive `closedStreamIdleTimeout` of at most 4,294,967,294 milliseconds to `YamuxProtocol`. After that long without a completed outbound data write, Yamux resets the stream. A peer that intentionally pauses reading longer than the configured timeout can also be reset, so leave it unset when such pauses are expected.

`YamuxWindowSettings.InitialWindowSize` configures the receive window advertised in SYN/ACK. Send credit always starts at the protocol default of 256 KiB and grows only through peer WindowUpdates, including the delta in a peer's SYN/ACK. The connection admits up to 512 concurrently tracked inbound streams; locally initiated streams do not consume that capacity. This transport cap is separate from application protocol limits and the unacknowledged stream backlog.

A normal GoAway closes admission in both directions while existing streams continue exchanging data, window credit, FIN and RST. New inbound streams receive RST, and new local dial requests fail with `ChannelClosedException`. The receive loop and transport remain active until disconnection or a fatal error. Nonzero GoAway codes terminate the connection. On transport loss, only a stream that received peer FIN preserves buffered bytes followed by EOF; other streams are aborted because their data may be truncated.

Duplicate SYNs for tracked stream IDs terminate the connection with GoAway(ProtocolError). Payloads discarded for rejected, reset or unknown streams have a ten-second control deadline. An incomplete or failed discard terminates the connection rather than parsing another header at an uncertain boundary.
