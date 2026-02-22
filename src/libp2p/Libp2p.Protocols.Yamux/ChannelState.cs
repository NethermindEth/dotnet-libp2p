// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Yamux;

internal class ChannelState(IChannel? channel = default, YamuxWindowSettings? windowSettings = null)
{
    public IChannel? Channel { get; set; } = channel;
    public LocalDataWindow LocalWindow { get; } = new(windowSettings);
    public RemoteDataWindow RemoteWindow { get; } = new(windowSettings?.InitialWindowSize ?? YamuxProtocol.ProtocolInitialWindowSize);
}
