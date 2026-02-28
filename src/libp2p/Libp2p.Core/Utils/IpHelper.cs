// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;

namespace Nethermind.Libp2p.Core.Utils;

public class IpHelper
{
    public static IEnumerable<IPAddress> GetListenerAddresses() => NetworkInterface.GetAllNetworkInterfaces().SelectMany(i => i.GetIPProperties().UnicastAddresses.Select(a => a.Address));
}
