# AutoNAT v2

Implements the server side of [AutoNAT v2](https://github.com/libp2p/specs/blob/master/autonat/autonat-v2.md) (`/libp2p/autonat/2/dial-request`).

The server picks the first direct literal IP address it can dial that passes
`IsPublicAddressCandidate`. Enable `AutoNatV2ServerSettings.AllowPrivateAddresses`
to allow direct IP addresses outside the public candidate ranges. When the
selected IP differs from the client's observed direct IP or that IP is unknown,
the server asks for 30-100 kB of dial data. It dials back through
`IAutoNatDialBack` and reports the result. Requests are rate limited with
`AutoNatV2ServerSettings`.

The client, reachability state and builder registration are not implemented yet.
