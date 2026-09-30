// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Yamux;

internal class ChannelState(IChannel? channel, YamuxWindowSettings windowSettings) : IDisposable
{
    private readonly CancellationTokenSource _outboundCancellation = new();

    public IChannel? Channel { get; set; } = channel;
    public LocalDataWindow LocalWindow { get; } = new(windowSettings);
    public RemoteDataWindow RemoteWindow { get; } = new(windowSettings.InitialWindowSize);
    public CancellationToken OutboundCancellation => _outboundCancellation.Token;

    public void AbortOutbound()
    {
        try { _outboundCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _outboundCancellation.Dispose();
}
