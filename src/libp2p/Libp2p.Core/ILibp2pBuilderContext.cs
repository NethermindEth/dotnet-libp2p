// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Stack;

public interface IProtocolStackSettings
{
    Dictionary<ProtocolRef, ProtocolRef[]>? Protocols { get; set; }
    ProtocolRef[]? TopProtocols { get; set; }
}
