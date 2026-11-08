// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address.Protocols;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal static class IpColocation
{
    /// <summary>Derives buckets only from the connected session's remote address, never a peer advertisement.</summary>
    internal static (string? Address, string? Subnet) GetKeys(Multiaddress address, IReadOnlyList<IPNetwork> whitelist)
    {
        if (address.Protocols.FirstOrDefault() is not IP { Value: IPAddress ip } ||
            address.Protocols.Any(p => p.Name is "p2p-circuit" or "dns" or "dns4" or "dns6" or "dnsaddr" or "ip6zone"))
        {
            // DNS is not resolved here; a relay's transport IP does not identify the destination.
            return (null, null);
        }

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) ||
            (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ||
            whitelist.Any(network => network.Contains(ip)))
        {
            return (null, null);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 contributes to both the exact address and /64, as in go-libp2p.
            // The suffix keeps subnet keys distinct even when the exact address ends in zeroes.
            byte[] prefix = ip.GetAddressBytes();
            prefix.AsSpan(8).Clear();
            return (ip.ToString(), $"{new IPAddress(prefix)}/64");
        }

        return (ip.ToString(), null);
    }
}
