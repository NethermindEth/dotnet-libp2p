// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Stack;

public interface IProtocolStackSettings
{
    Dictionary<IProtocol, IProtocol[]>? Protocols { get; set; }
    IProtocol[]? TopProtocols { get; set; }
}
