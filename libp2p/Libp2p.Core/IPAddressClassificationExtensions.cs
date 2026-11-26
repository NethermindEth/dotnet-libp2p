// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Core;

public static class IPAddressClassificationExtensions
{
    private static readonly IPNetwork UnspecifiedIPv4 = new(IPAddress.Any, 32);
    private static readonly IPNetwork UnspecifiedIPv6 = new(IPAddress.IPv6Any, 128);
    private static readonly IPNetwork LoopbackIPv4 = IPNetwork.Parse("127.0.0.0/8");
    private static readonly IPNetwork LoopbackIPv6 = new(IPAddress.IPv6Loopback, 128);
    private static readonly IPNetwork LinkLocalIPv4 = IPNetwork.Parse("169.254.0.0/16");
    private static readonly IPNetwork[] PrivateIPv4 =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16")
    ];

    // IANA IPv4 special-purpose ranges, plus multicast.
    // https://www.iana.org/assignments/iana-ipv4-special-registry
    private static readonly IPNetwork[] NonPublicIPv4 =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        .. PrivateIPv4,
        IPNetwork.Parse("100.64.0.0/10"),
        LoopbackIPv4,
        LinkLocalIPv4,
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.31.196.0/24"),
        IPNetwork.Parse("192.52.193.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.175.48.0/24"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"), // Multicast
        IPNetwork.Parse("240.0.0.0/4") // Reserved and limited broadcast
    ];

    private static readonly IPNetwork GlobalUnicastIPv6 = IPNetwork.Parse("2000::/3");

    // IANA IPv6 special-purpose ranges within the assignable global unicast space.
    // https://www.iana.org/assignments/iana-ipv6-special-registry
    private static readonly IPNetwork[] NonPublicIPv6 =
    [
        IPNetwork.Parse("2001::/23"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("2620:4f:8000::/48"),
        IPNetwork.Parse("3fff::/20")
    ];

    /// <summary>Tests for IPv4 or IPv6 loopback, including IPv4-mapped loopback.</summary>
    public static bool IsLoopbackAddress(this IPAddress address)
        => IsIPv4(address) ? LoopbackIPv4.Contains(address) : LoopbackIPv6.Contains(address);

    /// <summary>Tests for 0.0.0.0, ::, or an IPv4-mapped 0.0.0.0.</summary>
    public static bool IsUnspecified(this IPAddress address)
        => IsIPv4(address) ? UnspecifiedIPv4.Contains(address) : UnspecifiedIPv6.Contains(address);

    /// <summary>Tests for IPv4 or IPv6 link-local unicast, including IPv4-mapped link-local.</summary>
    public static bool IsLinkLocal(this IPAddress address)
        => address.IsIPv6LinkLocal || LinkLocalIPv4.Contains(address);

    /// <summary>Tests for RFC 1918 IPv4, IPv6 unique-local, or deprecated IPv6 site-local space.</summary>
    /// <remarks>IPv4-mapped addresses use their embedded IPv4 range.</remarks>
    public static bool IsPrivateAddress(this IPAddress address)
        => IsIPv4(address) ? Contains(PrivateIPv4, address) : address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;

    /// <summary>Tests whether the IP is a candidate for a public peer endpoint.</summary>
    /// <remarks>
    /// Candidates are unscoped unicast literals outside the IANA special-purpose ranges.
    /// Protocol-specific and anycast reservations are excluded even if IANA lists them as globally
    /// reachable. Native IPv6 candidates must fall within 2000::/3. IPv4-mapped addresses use their
    /// embedded IPv4 range. This test cannot establish assignment, routing, or reachability.
    /// </remarks>
    public static bool IsPublicAddressCandidate(this IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
        {
            return false;
        }

        return IsIPv4(address)
            ? !Contains(NonPublicIPv4, address)
            : GlobalUnicastIPv6.Contains(address) && !Contains(NonPublicIPv6, address);
    }

    private static bool IsIPv4(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork || address.IsIPv4MappedToIPv6;

    private static bool Contains(IPNetwork[] networks, IPAddress address)
    {
        foreach (IPNetwork network in networks)
        {
            if (network.Contains(address)) return true;
        }

        return false;
    }
}
