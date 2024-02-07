// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

public class IdentifyProtocolSettings
{
    public string? AgentVersion { get; set; }
    public string? ProtocolVersion { get; set; }

    public static IdentifyProtocolSettings Default { get; } = new()
    {
        ProtocolVersion = "ipfs/1.0.0",
        AgentVersion = "dotnet-libp2p/1.0.0",
    };
}
