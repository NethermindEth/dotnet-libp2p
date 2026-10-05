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
        if (addr is null)
        {
            return default;
        }

        string[] segments = addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = segments.Length - 2; i >= 0; i--)
        {
            if (segments[i].Equals("p2p", StringComparison.OrdinalIgnoreCase))
            {
                return new PeerId(segments[i + 1]);
            }
        }

        return default;
    }

    public static Multiaddress GetEndpointPart(this Multiaddress multiaddress)
        => multiaddress.ToEndPoint(out ProtocolType proto).ToMultiaddress(proto);
}
