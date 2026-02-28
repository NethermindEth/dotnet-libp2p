// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public class ProtocolStackSettings : IProtocolStackSettings
{
    public ProtocolRef[]? TopProtocols { get; set; }
    public Dictionary<ProtocolRef, ProtocolRef[]>? Protocols { get; set; }
}
