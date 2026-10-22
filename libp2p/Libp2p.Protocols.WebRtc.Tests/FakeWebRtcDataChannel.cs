// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// In-memory data channel. Delivery to the paired channel is synchronous, so a test reaches a
/// quiescent point the moment a send returns and never has to sleep or poll. Buffered amount is
/// driven by the test rather than by sends, and closing one side does not notify the other, which
/// keeps the synchronous delivery free of reentrancy.
/// </summary>
internal sealed class FakeWebRtcDataChannel : IWebRtcDataChannel
{
    private readonly List<byte[]> _sent = [];

    public event Action<byte[]>? MessageReceived;
    public event Action? BufferedAmountLow;
    public event Action? Closed;

    public FakeWebRtcDataChannel? Peer { get; private set; }

    public long BufferedAmount { get; private set; }

    public bool IsClosed { get; private set; }

    public IReadOnlyList<byte[]> SentFrames
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public static (FakeWebRtcDataChannel A, FakeWebRtcDataChannel B) CreatePair()
    {
        FakeWebRtcDataChannel a = new();
        FakeWebRtcDataChannel b = new();
        a.Peer = b;
        b.Peer = a;
        return (a, b);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default)
    {
        byte[] copy = frame.ToArray();
        lock (_sent)
        {
            _sent.Add(copy);
        }

        if (Peer is { IsClosed: false })
        {
            Peer.Deliver(copy);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Hands raw bytes to the stream as one data channel message, framing and all.</summary>
    public void Deliver(byte[] message) => MessageReceived?.Invoke(message);

    public void SetBufferedAmount(long value) => BufferedAmount = value;

    public void Drain()
    {
        BufferedAmount = 0;
        BufferedAmountLow?.Invoke();
    }

    public void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        Closed?.Invoke();
    }
}
