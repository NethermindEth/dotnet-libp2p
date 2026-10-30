// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using SIPSorcery.Net;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// One SCTP stream of a live association, presented as the <see cref="IWebRtcDataChannel"/> slice
/// <see cref="WebRtcStream"/> consumes. Implementing that interface rather than an adapter on top of
/// it is the whole point: the same stream state machine runs over this in production and over
/// FakeWebRtcDataChannel in CI, where no DTLS stack is available.
/// </summary>
internal sealed class SctpWebRtcDataChannel : IWebRtcDataChannel
{
    /// <summary>
    /// Inbound datachannel messages held between the association's receive thread and the pump.
    /// Deep enough to absorb a burst, shallow enough that a stalled consumer is reported rather
    /// than hidden.
    /// </summary>
    private const int MaxBufferedMessages = 256;

    private readonly SctpAssociation _association;

    /// <summary>
    /// A single SCTP receive thread feeds every channel on the association, so messages are handed
    /// to a pump instead of being dispatched on it: one slow subscriber must not stall the other
    /// channels. <see cref="BoundedChannelFullMode.Wait"/> is what makes the TryWrite in
    /// <see cref="Receive"/> a genuine overflow signal; with DropWrite it would always report
    /// success and silently discard.
    /// </summary>
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _closed;

    public SctpWebRtcDataChannel(SctpAssociation association, ushort streamId)
    {
        _association = association;
        StreamId = streamId;
        _ = Task.Run(PumpAsync);
    }

    public event Action<byte[]>? MessageReceived;

    public event Action? BufferedAmountLow;

    public event Action? Closed;

    public ushort StreamId { get; }

    /// <summary>Completes once the DCEP handshake for this stream has finished.</summary>
    public Task Opened => _opened.Task;

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>
    /// SIPSorcery queues outbound user data per association, not per stream: the only send-buffer
    /// signal it exposes is <see cref="SctpAssociation.SendBufferedAmount"/>, the byte count still
    /// sitting in the association's send queue (chunks already on the wire but unacknowledged are
    /// not counted, same as a browser's RTCDataChannel.bufferedAmount). So this reports the whole
    /// association's backlog rather than this stream's share of it. That over-reports, never
    /// under-reports, so the stream applies backpressure early; the cost is that a burst on one
    /// stream parks the writers on its siblings until the association drains.
    /// </summary>
    public long BufferedAmount => (long)_association.SendBufferedAmount;

    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        try
        {
            // Enqueues on the association's send queue and returns; the sender thread does the
            // writing, which is why BufferedAmount is the thing to watch rather than this call.
            // Still attempted after Close(): closing is local bookkeeping only (see Close), and
            // the stream's teardown flags have to be able to reach the peer.
            _association.SendData(StreamId, (uint)DataChannelPayloadProtocols.WebRTC_Binary, frame.ToArray());
        }
        catch (Exception ex)
        {
            Close();
            throw new IOException($"Failed to send on SCTP stream {StreamId}.", ex);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Drops this side's view of the stream. There is no SCTP stream reset behind it: SIPSorcery
    /// implements no RFC 6525 RE-CONFIG chunk, so the stream id stays spent for the lifetime of the
    /// association (see <see cref="WebRtcDirectSctpMuxer"/>). Idempotent.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        _incoming.Writer.TryComplete();
        _opened.TrySetCanceled();
        Closed?.Invoke();
    }

    internal void MarkOpen() => _opened.TrySetResult();

    /// <summary>
    /// Queues one inbound frame. DCEP never reaches here -- RTCPeerSctpAssociation routes it to its
    /// own events -- so everything that arrives is user data for the stream.
    /// </summary>
    internal void Receive(SctpDataFrame frame)
    {
        // An empty WebRTC message is carried as one padding byte under an *_Empty PPID; forwarding
        // that byte would corrupt frame reassembly in WebRtcStream.
        if (frame.PPID is (uint)DataChannelPayloadProtocols.WebRTC_Binary_Empty
                       or (uint)DataChannelPayloadProtocols.WebRTC_String_Empty
            || frame.UserData is not { Length: > 0 })
        {
            return;
        }

        if (!_incoming.Writer.TryWrite(frame.UserData))
        {
            _incoming.Writer.TryComplete(
                new IOException($"Inbound buffer overflow on SCTP stream {StreamId}: more than {MaxBufferedMessages} messages are unread."));
        }
    }

    /// <summary>
    /// Raises <see cref="BufferedAmountLow"/> while the send queue sits below
    /// <see cref="WebRtcStream.MaxBufferedAmount"/>. SCTP offers no drain notification, so
    /// <see cref="WebRtcDirectSctpMuxer"/> polls this.
    ///
    /// INVARIANT: the event must never fire at or above <see cref="WebRtcStream.MaxBufferedAmount"/>.
    /// A writer parked in WaitForCapacityAsync wakes on it, re-checks <see cref="BufferedAmount"/>
    /// and parks again, so a threshold above that limit would leave it parked forever.
    ///
    /// It fires on every poll below the threshold rather than only on the falling edge: an edge
    /// between two polls would be missed outright, whereas a spurious wake costs one re-check and
    /// hits no waiter at all when none is parked.
    /// </summary>
    internal void PollBufferedAmount(long bufferedAmount)
    {
        if (bufferedAmount < WebRtcStream.MaxBufferedAmount)
        {
            BufferedAmountLow?.Invoke();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (byte[] message in _incoming.Reader.ReadAllAsync())
            {
                MessageReceived?.Invoke(message);
            }
        }
        catch (Exception)
        {
            // Overflow, or a subscriber that threw. The seam has no error channel, so the stream
            // learns about it the only way it can: the channel going away under it, which it
            // already treats as a truncated transfer rather than a clean end.
        }

        Close();
    }
}
