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
    {
        P2P? destination = null;
        if (addr is null) return null;

        foreach (var protocol in addr.Protocols)
        {
            // A circuit starts a new destination; preceding peer IDs identify relays.
            if (protocol is P2PCircuit) destination = null;
            else if (protocol is P2P peer) destination = peer;
        }

        return destination is null ? null : new PeerId(destination.Value is null ? [] : [.. destination.ToBytes()]);
    }

    public static Multiaddress GetEndpointPart(this Multiaddress multiaddress)
        => multiaddress.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto);
}
