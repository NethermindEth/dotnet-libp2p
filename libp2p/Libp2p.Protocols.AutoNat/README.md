# AutoNAT v2

Implements the server side of [AutoNAT v2](https://github.com/libp2p/specs/blob/master/autonat/autonat-v2.md) (`/libp2p/autonat/2/dial-request`).

The server picks the first public address it can dial, asks for 30-100 kB of dial data when that address's IP differs from the client's observed IP, then dials back through `IAutoNatDialBack` and reports the result. Requests are rate limited with `AutoNatV2ServerSettings`.

The client, reachability state and builder registration are not implemented yet.
