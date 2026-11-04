// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address.Protocols;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal static class IpColocation
{
    /// <summary>Derives a bucket only from the connected session's remote address, never a peer advertisement.</summary>
    internal static string? GetKey(Multiaddress address, IReadOnlyList<IPNetwork> whitelist)
    {
        if (address.Protocols.FirstOrDefault() is not IP { Value: IPAddress ip } ||
            address.Protocols.Any(p => p.Name is "p2p-circuit" or "dns" or "dns4" or "dns6" or "dnsaddr" or "ip6zone"))
        {
            // DNS is not resolved here; a relay's transport IP does not identify the destination.
            return null;
        }

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ||
            whitelist.Any(network => network.Contains(ip)))
        {
            return null;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Group IPv6 by /64; the suffix keeps prefix keys distinct from literal addresses.
            byte[] prefix = ip.GetAddressBytes();
            prefix.AsSpan(8).Clear();
            return $"{new IPAddress(prefix)}/64";
        }

        return ip.ToString();
    }
}
