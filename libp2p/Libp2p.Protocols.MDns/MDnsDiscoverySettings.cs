// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

public class MDnsDiscoverySettings
{
    /// <summary>The service name the libp2p mDNS specification defines for peer discovery.</summary>
    public const string Libp2pServiceName = "_p2p._udp.local";

    /// <summary>
    /// DNS-SD service name to advertise and query, <see cref="Libp2pServiceName"/> by default.
    /// <para>
    /// Setting anything else confines discovery to peers configured with the same name. That is an
    /// opt-in for a private network, and it also makes this node invisible to every standard libp2p
    /// mDNS peer, including go-libp2p and js-libp2p.
    /// </para>
    /// </summary>
    public string ServiceName { get; set; } = Libp2pServiceName;
}
