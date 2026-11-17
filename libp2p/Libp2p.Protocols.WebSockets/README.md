# WebSockets transport

WebSocket and Secure WebSocket transport support for dotnet-libp2p.

Add `WebSocketProtocol` to `StreamTransports` of a builder derived from
`Libp2pPeerFactoryBuilder` to enable `/ws` and `/wss` multiaddrs. The
`Nethermind.Libp2p` package README shows an example. Secure WebSocket listeners require an `ITlsCertificateProvider`,
which can be supplied by `Libp2p.Protocols.AutoTls`.
