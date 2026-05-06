# WebSockets transport

WebSocket and Secure WebSocket transport support for dotnet-libp2p.

Use `WithWebSockets()` when building a libp2p peer to enable `/ws` and `/wss`
multiaddrs. Secure WebSocket listeners require an `ITlsCertificateProvider`,
which can be supplied by `Libp2p.Protocols.AutoTls`.
