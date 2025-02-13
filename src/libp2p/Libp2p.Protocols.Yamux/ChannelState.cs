// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Yamux;

internal class ChannelState(IChannel? channel = default)
{
    public IChannel? Channel { get; set; } = channel;
    public LocalDataWindow LocalWindow { get; } = new();
    public RemoteDataWindow RemoteWindow { get; } = new();
}
