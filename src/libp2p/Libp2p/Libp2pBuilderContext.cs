// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Stack;

internal class Libp2pBuilderContext : IBuilderContext
{
    public IProtocol[]? TopProtocols { get; set; }
}
