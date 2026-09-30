# Yamux protocol

See the [libp2p spec](https://github.com/libp2p/specs/tree/master/yamux)

By default, a locally closed stream waits for its outbound data to drain or for the connection to close. Applications that need to bound retained stream state can pass a positive `closedStreamIdleTimeout` to `YamuxProtocol`. After that long without a completed outbound data write, Yamux resets the stream. A peer that intentionally pauses reading longer than the configured timeout can also be reset, so leave it unset when such pauses are expected.
