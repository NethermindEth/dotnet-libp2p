// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Yamux;

internal class ChannelState(IChannel? channel, YamuxWindowSettings windowSettings, bool isInbound = false) : IDisposable
{
    private readonly CancellationTokenSource _outboundCancellation = new();
    private volatile bool _finReceived;

    public bool IsInbound { get; } = isInbound;

    public IChannel? Channel { get; set; } = channel;

    /// <summary>Set by the session loop once the peer's FIN arrived; read by the session loop and the outbound pump when settling the stream.</summary>
    public bool FinReceived { get => _finReceived; set => _finReceived = value; }
    public LocalDataWindow LocalWindow { get; } = new(windowSettings);
    public RemoteDataWindow RemoteWindow { get; } = new(YamuxProtocol.ProtocolInitialWindowSize);
    public CancellationToken OutboundCancellation => _outboundCancellation.Token;

    public void AbortOutbound()
    {
        try { _outboundCancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose() => _outboundCancellation.Dispose();
}
