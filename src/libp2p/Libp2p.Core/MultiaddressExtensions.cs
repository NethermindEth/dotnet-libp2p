// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Net;
using Multiformats.Address.Protocols;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Core;

public static class MultiaddressExtensions
{
    public static PeerId? GetPeerId(this Multiaddress? addr)
        => addr is not null && addr.Has<P2P>() ? new PeerId(addr.Get<P2P>().ToString()) : default;

    public static Multiaddress GetEndpointPart(this Multiaddress multiaddress)
        => multiaddress.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto);
}
