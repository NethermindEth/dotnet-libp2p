// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Stack;

internal class ProtocolStackSettings : IProtocolStackSettings
{
    public ProtocolRef[]? TopProtocols { get; set; }
    public Dictionary<ProtocolRef, ProtocolRef[]>? Protocols { get; set; }
}
