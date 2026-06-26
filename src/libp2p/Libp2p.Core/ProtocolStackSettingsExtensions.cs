// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

internal static class ProtocolStackSettingsExtensions
{
    public static IEnumerable<IProtocol> GetDistinctProtocols(this IProtocolStackSettings protocolStackSettings)
    {
        HashSet<IProtocol> protocols = [];
        if (protocolStackSettings.TopProtocols is not null)
        {
            foreach (ProtocolRef protocolRef in protocolStackSettings.TopProtocols)
            {
                protocols.Add(protocolRef.Protocol);
            }
        }

        if (protocolStackSettings.Protocols is not null)
        {
            foreach ((ProtocolRef parent, ProtocolRef[] children) in protocolStackSettings.Protocols)
            {
                protocols.Add(parent.Protocol);
                foreach (ProtocolRef child in children)
                {
                    protocols.Add(child.Protocol);
                }
            }
        }

        return protocols;
    }
}
