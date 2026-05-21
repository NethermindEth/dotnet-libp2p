// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.AutoTls.Internal;

namespace Nethermind.Libp2p.Protocols.AutoTls;

public static class AutoTlsDomain
{
    public static string GetPeerDomain(PeerId peerId, string forgeDomain = AutoTlsOptions.DefaultForgeDomain)
    {
        ArgumentNullException.ThrowIfNull(peerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(forgeDomain);

        return $"{PeerIdEncoding.ToBase36CidString(peerId)}.{forgeDomain}";
    }

    public static string GetIpHost(PeerId peerId, IPAddress ipAddress, string forgeDomain = AutoTlsOptions.DefaultForgeDomain)
    {
        ArgumentNullException.ThrowIfNull(peerId);
        ArgumentNullException.ThrowIfNull(ipAddress);

        string encodedIp = ipAddress.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => ipAddress.ToString().Replace('.', '-'),
            _ => throw new NotSupportedException("Only IPv4 libp2p.direct hostnames are currently supported.")
        };
        return $"{encodedIp}.{GetPeerDomain(peerId, forgeDomain)}";
    }
}
