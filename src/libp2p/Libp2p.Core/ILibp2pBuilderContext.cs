// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IProtocolStackSettings
{
    Dictionary<ProtocolRef, ProtocolRef[]>? Protocols { get; set; }
    ProtocolRef[]? TopProtocols { get; set; }
}
