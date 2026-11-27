// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Identify;

public static class IPAddressExtensions
{
    /// <summary>Tests for loopback, link-local or private-use IP addresses.</summary>
    public static bool IsPrivate(this IPAddress ip)
        => ip.IsLoopbackAddress() || ip.IsLinkLocal() || ip.IsPrivateAddress();
}
