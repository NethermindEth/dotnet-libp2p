// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using SIPSorcery.Net;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// A live SIPSorcery <see cref="RTCDataChannel"/> presented as the <see cref="IWebRtcDataChannel"/>
/// slice <see cref="WebRtcStream"/> consumes, so what reaches the wire is the spec's
/// length-prefixed <c>webrtc.pb.Message</c> framing rather than whatever bytes a caller happened to
/// write. The sibling <see cref="SctpWebRtcDataChannel"/> implements the same seam straight off an
/// SCTP association; this one is for the channels RTCPeerConnection hands out.
/// </summary>
internal sealed class RtcDataChannelAdapter : IWebRtcDataChannel, IDisposable
{
    /// <summary>
    /// How often <see cref="BufferedAmount"/> is sampled to wake a parked writer. Mirrors
    /// <see cref="WebRtcDirectSctpMuxer"/>: short enough not to show up as throughput loss, long
    /// enough that the sampling itself is noise.
    /// </summary>
    private static readonly TimeSpan BufferedAmountPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// INVARIANT: strictly below <see cref="WebRtcStream.MaxBufferedAmount"/>. A writer parked in
    /// WaitForCapacityAsync wakes on the event, re-checks <see cref="BufferedAmount"/> and parks
    /// again, so a threshold at or above that limit would leave it parked forever. Half the limit
    /// also leaves a frame still queued when the writer is released, so the channel is refilled
    /// before it runs dry.
    /// </summary>
    private const long BufferedAmountLowThreshold = WebRtcStream.MaxBufferedAmount / 2;

    private readonly RTCDataChannel _channel;
    private readonly Timer _bufferedAmountPoll;
    private int _closed;

    public RtcDataChannelAdapter(RTCDataChannel channel)
    {
        // Captured rather than read through: RTCDataChannel.id is nullable and is assigned when the
        // channel joins the peer connection's collection, so a channel that reached here without
        // one would be unusable -- SIPSorcery's own send() would address stream 0 for it.
        StreamId = channel.id ?? throw new ArgumentException("The RTC data channel has no stream id.", nameof(channel));
        _channel = channel;
        _channel.onmessage += OnMessage;
        _channel.onclose += Close;
        _channel.onerror += OnError;

        // Started last, so no tick can run before the handlers above are wired.
        _bufferedAmountPoll = new Timer(_ => PollBufferedAmount(), null, BufferedAmountPollInterval, BufferedAmountPollInterval);
    }

    /// <summary>
    /// Buffered until a consumer attaches. <c>RTCDataChannel.GotData</c> invokes its event with no
    /// queue behind it, and RFC 8832 section 6 lets a peer send data before the OPEN/ACK exchange
    /// finishes, so anything arriving between this adapter being constructed and the stream above it
    /// subscribing used to be dropped -- which is a lost multistream header, not a lost byte.
    /// </summary>
    public event Action<byte[]>? MessageReceived
    {
        add
        {
            _messageReceived += value;
            if (Interlocked.Exchange(ref _pumping, 1) == 0)
            {
                _ = Task.Run(PumpAsync);
            }
        }
        remove => _messageReceived -= value;
    }

    /// <summary>
    /// Matches <see cref="SctpWebRtcDataChannel"/>'s inbound depth, and for the same reason: deep
    /// enough to absorb a burst before the consumer attaches, shallow enough that a stream nobody
    /// ever reads is bounded.
    /// </summary>
    private const int MaxPendingMessages = 256;

    /// <summary>
    /// One ordered path for retained and live messages alike. An earlier version published the
    /// subscriber and then replayed a backlog outside the lock, which let a message arriving during
    /// the replay overtake the queued ones -- and since the framing above splits payloads across
    /// messages, a reordering there is a corrupt frame rather than a late one. A single reader over
    /// one bounded queue cannot reorder.
    /// </summary>
    private readonly Channel<byte[]> _incoming = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxPendingMessages)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    private Action<byte[]>? _messageReceived;
    private int _pumping;

    public event Action? BufferedAmountLow;

    public event Action? Closed;

    public ushort StreamId { get; }

    public bool IsClosed => Volatile.Read(ref _closed) == 1;

    /// <summary>
    /// <c>RTCDataChannel.bufferedAmount</c> is a <see cref="ulong"/> count of bytes queued on the
    /// SCTP association and not yet written, the same quantity a browser's
    /// <c>RTCDataChannel.bufferedAmount</c> reports.
    /// </summary>
    public long BufferedAmount => (long)_channel.bufferedAmount;

    /// <summary>
    /// SIPSorcery 10.0.14 declares <c>void send(byte[] data, int offset = 0, int count = -1)</c>,
    /// which enqueues on the association and returns; congestion shows up in
    /// <see cref="BufferedAmount"/>, never as a block here.
    /// </summary>
    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        try
        {
            // Copied rather than handed over as a slice: send() keeps the array until the sender
            // thread has drained it, and the seam does not promise the caller's buffer outlives
            // this call.
            _channel.send(frame.ToArray());
        }
        catch (Exception ex)
        {
            // A send that cannot reach the transport means the channel is gone; say so now rather
            // than leave the stream writing into it.
            Close();
            throw new IOException($"Failed to send on RTC data channel '{_channel.label}'.", ex);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Idempotent, and the single place <see cref="Closed"/> is raised, so the peer's close, an
    /// error, a failed send and the stream's own teardown all collapse into one notification.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return;
        }

        // Disposed before anything that can block or re-enter: that stops further ticks being
        // scheduled, and a tick already in flight finds _closed set and returns without touching
        // the channel. Timer.Dispose does not wait for an in-flight callback, so the flag -- not
        // this call -- is what makes a late tick harmless.
        _bufferedAmountPoll.Dispose();
        _incoming.Writer.TryComplete();

        try
        {
            // Re-entrant: close() fires onclose, which lands back here and returns on the flag.
            _channel.close();
        }
        catch (Exception)
        {
            // A channel whose transport has already gone throws here. It is closed either way, and
            // the subscriber below still has to be told.
        }

        Closed?.Invoke();
    }

    /// <summary>Owners that only know about <see cref="IDisposable"/> still release the timer.</summary>
    public void Dispose() => Close();

    private void OnMessage(RTCDataChannel channel, DataChannelPayloadProtocols protocol, byte[] data)
    {
        // RFC 8831 section 6.6 carries an empty application message as a single zero padding byte
        // under an *_Empty PPID, and RTCDataChannel.GotData forwards that byte verbatim (verified
        // against SIPSorcery 10.0.14). Passing it on would insert a stray byte into the frame
        // reassembly buffer in WebRtcStream and desynchronize every frame after it.
        if (protocol is DataChannelPayloadProtocols.WebRTC_Binary_Empty or DataChannelPayloadProtocols.WebRTC_String_Empty
            || data is not { Length: > 0 })
        {
            return;
        }

        if (!_incoming.Writer.TryWrite(data))
        {
            // Failed rather than discarded. SCTP has already delivered these bytes and will not
            // retransmit them, so dropping one while keeping the channel open silently truncates a
            // payload -- or loses a FIN. Completing with the fault keeps everything retained so far
            // and hands the failure to whichever consumer eventually attaches.
            _incoming.Writer.TryComplete(
                new IOException($"Inbound buffer overflow on RTC data channel '{_channel.label}': more than {MaxPendingMessages} messages are unread."));
        }
    }

    /// <summary>
    /// Drains the retained queue, and then every later message, in arrival order. Started by the
    /// first subscriber rather than in the constructor, so nothing is drained into a null event
    /// before the stream above has attached.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            await foreach (byte[] message in _incoming.Reader.ReadAllAsync())
            {
                _messageReceived?.Invoke(message);
            }
        }
        catch (Exception)
        {
            // Overflow, or a subscriber that threw. The seam has no error channel, so the stream
            // learns of it the only way it can: the channel going away under it, which it already
            // treats as a truncated transfer rather than a clean end.
        }

        Close();
    }

    /// <summary>
    /// <c>onerror</c> carries only a string and the seam has no error channel, so an error is
    /// reported as a close. Treating it as anything less than terminal is what leaves a stream
    /// parked forever: SIPSorcery does not follow <c>onerror</c> with <c>onclose</c>, and an
    /// errored channel is not coming back.
    /// </summary>
    private void OnError(string error) => Close();

    /// <summary>
    /// RTCDataChannel has a settable <c>bufferedAmountLowThreshold</c> property but raises no event
    /// off it -- there is no <c>onbufferedamountlow</c> member on the type in SIPSorcery 10.0.14 --
    /// so the browser event the spec's backpressure assumes has to be synthesized by sampling, as
    /// <see cref="WebRtcDirectSctpMuxer"/> already does for its own channels.
    /// <para>
    /// Fires on every tick below the threshold rather than only on the falling edge: an edge
    /// between two samples would be missed outright, whereas a spurious wake costs the writer one
    /// re-check and hits no waiter at all when none is parked.
    /// </para>
    /// </summary>
    private void PollBufferedAmount()
    {
        if (Volatile.Read(ref _closed) == 1)
        {
            return;
        }

        try
        {
            if (BufferedAmount < BufferedAmountLowThreshold)
            {
                BufferedAmountLow?.Invoke();
            }
        }
        catch (Exception)
        {
            // Timer callbacks run on the pool with nobody to observe them: a throwing subscriber,
            // or a property read against a peer connection disposed under us, would otherwise take
            // the process down. Losing a wake-up only costs one more interval of latency, and the
            // next tick retries -- unless Close() ran, in which case the stream is already failing.
        }
    }
}
