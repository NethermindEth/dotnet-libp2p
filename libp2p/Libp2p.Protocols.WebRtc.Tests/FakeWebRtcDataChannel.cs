// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// In-memory data channel. Delivery to the paired channel is synchronous, so every frame and flag
/// has been handled the moment a send returns; only payload still has to cross the receiving
/// channel's delivery pump before a read sees it. Buffered amount is driven by the test rather than
/// by sends, and closing one side does not notify the other, which keeps the synchronous delivery
/// free of reentrancy.
/// </summary>
internal sealed class FakeWebRtcDataChannel : IWebRtcDataChannel
{
    private readonly List<byte[]> _sent = [];

    public event Action<byte[]>? MessageReceived;
    public event Action? BufferedAmountLow;
    public event Action? Closed;

    public FakeWebRtcDataChannel? Peer { get; private set; }

    public ushort StreamId { get; init; }

    private long _bufferedAmount;

    /// <summary>
    /// Runs on each read of <see cref="BufferedAmount"/>, so a test can interleave an event with
    /// the exact point the stream samples congestion -- which is the only way to land inside the
    /// window between that sample and the waiter being registered.
    /// </summary>
    public Action? OnBufferedAmountRead { get; set; }

    public long BufferedAmount
    {
        get
        {
            OnBufferedAmountRead?.Invoke();
            return _bufferedAmount;
        }
    }

    public bool IsClosed { get; private set; }

    public Action? OnClose { get; set; }

    public Action<byte[]>? BeforeSend { get; set; }

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
        BeforeSend?.Invoke(copy);
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

    public void SetBufferedAmount(long value) => _bufferedAmount = value;

    public void Drain()
    {
        _bufferedAmount = 0;
        BufferedAmountLow?.Invoke();
    }

    public void Close()
    {
        if (IsClosed)
        {
            return;
        }

        IsClosed = true;
        OnClose?.Invoke();
        Closed?.Invoke();
    }
}
