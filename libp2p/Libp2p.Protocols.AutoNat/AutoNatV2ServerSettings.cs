// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

/// <summary>AutoNAT v2 server limits. Defaults match go-libp2p.</summary>
public class AutoNatV2ServerSettings
{
    public int RequestsPerMinute { get; set; } = 60;
    public int RequestsPerPeerPerMinute { get; set; } = 12;
    public int DialDataRequestsPerMinute { get; set; } = 12;
    public int MaxConcurrentRequestsPerPeer { get; set; } = 2;
    public bool AllowPrivateAddresses { get; set; }

    /// <summary>Upper bound of the random pause before dialing an address that needed dial data.</summary>
    public TimeSpan MaxDialDataDelay { get; set; } = TimeSpan.FromSeconds(3);
}
