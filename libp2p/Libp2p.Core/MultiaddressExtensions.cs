// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Net;
using Multiformats.Address.Protocols;
using System.Net;
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

    /// <summary>Gets the leading literal IP of a direct address, preserving its address family.</summary>
    /// <remarks>
    /// DNS names, relay paths, non-IP endpoints, and addresses with multiple hosts return null.
    /// The address is read as supplied, including its transport and peer components. No lookup
    /// is performed.
    /// </remarks>
    public static IPAddress? GetDirectIPAddress(this Multiaddress? address)
    {
        if (address is null || address.Protocols.Count == 0) return null;

        IPAddress? ip = address.Protocols[0] switch
        {
            IP4 { Value: IPAddress { AddressFamily: AddressFamily.InterNetwork } ipv4 } => ipv4,
            IP6 { Value: IPAddress { AddressFamily: AddressFamily.InterNetworkV6 } ipv6 } => ipv6,
            _ => null
        };
        if (ip is null) return null;

        for (int i = 1; i < address.Protocols.Count; i++)
        {
            if (address.Protocols[i] is IP or DNS or DNS4 or DNS6 or DnsAddr or Unix or Onion or P2PCircuit)
            {
                return null;
            }
        }

        return ip;
    }

    /// <summary>Tests whether the direct literal IP is loopback.</summary>
    public static bool IsLoopbackAddress(this Multiaddress? address)
        => address.GetDirectIPAddress()?.IsLoopbackAddress() ?? false;

    /// <summary>Tests whether the direct literal IP is unspecified.</summary>
    public static bool IsUnspecified(this Multiaddress? address)
        => address.GetDirectIPAddress()?.IsUnspecified() ?? false;

    /// <summary>Tests whether the direct literal IP is link-local unicast.</summary>
    public static bool IsLinkLocal(this Multiaddress? address)
        => address.GetDirectIPAddress()?.IsLinkLocal() ?? false;

    /// <summary>Tests whether the direct literal IP is private-use, unique-local or site-local.</summary>
    public static bool IsPrivateAddress(this Multiaddress? address)
        => address.GetDirectIPAddress()?.IsPrivateAddress() ?? false;

    /// <summary>Tests whether the direct literal IP is a candidate for a public peer endpoint.</summary>
    /// <remarks>Transport support and reachability require separate checks by the caller.</remarks>
    public static bool IsPublicAddressCandidate(this Multiaddress? address)
        => address.GetDirectIPAddress()?.IsPublicAddressCandidate() ?? false;
}
