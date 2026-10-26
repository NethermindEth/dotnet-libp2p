// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols;

public class IdentifyProtocolSettings
{
    public string AgentVersion { get; set; } = "ipfs/1.0.0";
    public string ProtocolVersion { get; set; } = "dotnet-libp2p/1.0.0";
    public PeerRecordsVerificationPolicy PeerRecordsVerificationPolicy { get; set; } = PeerRecordsVerificationPolicy.RequireWithWarning;
}


public enum PeerRecordsVerificationPolicy
{
    RequireCorrect,
    RequireWithWarning,
    DoesNotRequire
}
