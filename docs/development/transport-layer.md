# Developing a lower layer protocol

There are to kinds of protocols below session ones:

- transport protocols that is responsible for establishing connection via `tcp`/`udp`
- connection protocols that handle encryption, authentication and establishing sessions `multistream-select`, `nois`, `yamux`, etc

A protocol may cover several aspects also, like `quic-v1` does for example

**Session** is an important concept in dotnet-libp2p. The client code can access established session to gain information about peers, dial peers with app layer protocols and manage connections. The entire stack of the protocols works for establishing such sessions.

The usual routine of a protocol is to:

- Wait for connection by listening or dial actively;
- Make a handshake;
- Start an upper layer protocol and redirect communication to it.

## Transport protocol

Transport layer protocols implement `ITransportProtocol`, their goal is to upgrade to connected state. When real connection is established, the implementation has to:

- create connection context via `ITransportContext.CreateConnection`
- upgrade to the upper protocol, by spawning a channel via `connectionContext.Upgrade()`
- send to and received data from that upper channel, passing it from and to the remote peer

Additionally

- listener should inform libp2p that it's ready, using `context.ListenerReady`
- listener has to share real addresses used for listening
- both listener and dialer parts should fill out `context.State`

## Connection protocol

Connection protocols are different from transport ones: they receive data from a channel below, their aim is to:

- negotiate upper level protocols
- ensure correctness of remote peer information like their public key, id, etc
- encrypt/decrypt data
- enable simultaneous communications via different protocols, via multiplexing

Usually the top level connection protocol is responsible for starting up session.
Transport layer protocol can do all connection protocol does.

Connection layer protocols typically do some of this:

- upgrade to the upper protocol, by spawning a channel via `context.Upgrade()`
- set `context.State.RemotePublicKey` / adjust `context.State.RemoteAddress`
- create session context via `IConnectionContext.UpgradeToSession()`
- send to and received data from that upper channel, passing it from and to the down channel

## Adding protocol to stack

In case you develop for libp2p protocol, you may want to include it in `Libp2pPeerFactoryBuilder`. See `BuildStack` implementation
Application layer protocols then can be added via a separate API(`AddAppLayerProtocol`)
