// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using SIPSorcery.Net;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// Turns one SCTP association into libp2p's stream muxer: outbound streams are opened with DCEP,
/// inbound ones are surfaced as an async stream, and each becomes an
/// <see cref="IWebRtcDataChannel"/> that <see cref="WebRtcStream"/> can drive.
/// </summary>
internal sealed class WebRtcDirectSctpMuxer : IAsyncDisposable
{
    /// <summary>
    /// The libp2p WebRTC spec runs the Noise handshake over the channel with stream id 0,
    /// negotiated out of band rather than with DCEP.
    /// </summary>
    public const ushort NoiseStreamId = 0;

    /// <summary>
    /// Ceiling on channels alive at once. The 16-bit id space would allow 65535, but a listener
    /// that lets one peer open that many hands it 65535 pump tasks and inbound queues for the cost
    /// of 12 bytes each; 256 matches the queue depth used elsewhere in this transport and is well
    /// above what any libp2p protocol set needs concurrently. A refused open is a protocol error
    /// for the peer, not a silent drop of data on an accepted stream.
    /// </summary>
    public const int MaxConcurrentChannels = 256;

    /// <summary>
    /// How often <see cref="SctpWebRtcDataChannel.PollBufferedAmount"/> runs. SCTP gives no
    /// send-buffer drain notification, so a parked writer is woken by polling; short enough not to
    /// show up as throughput loss, long enough that the O(send queue) scan behind
    /// <see cref="SctpAssociation.SendBufferedAmount"/> stays noise.
    /// </summary>
    private static readonly TimeSpan BufferedAmountPollInterval = TimeSpan.FromMilliseconds(20);

    private readonly RTCSctpTransport _transport;
    private readonly RTCPeerSctpAssociation _association;
    private readonly bool _isDtlsClient;
    private readonly ConcurrentDictionary<ushort, SctpWebRtcDataChannel> _channels = new();
    private readonly Channel<SctpWebRtcDataChannel> _inboundChannels = Channel.CreateUnbounded<SctpWebRtcDataChannel>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _streamIdLock = new();
    private readonly Timer _bufferedAmountPoll;

    /// <summary>
    /// Widened to int so exhaustion is a comparison rather than a wrap: as a ushort this rolls from
    /// 65534 to 0 and the next id handed out lands in the peer's half of the space.
    /// </summary>
    private int _nextOutboundStreamId;

    /// <summary>
    /// Every inbound id ever accepted. Ids are never reused, for the same reason outbound ids are
    /// not: with no RFC 6525 RE-CONFIG chunk available, a late chunk of a closed stream would be
    /// delivered into its replacement. Bounded by the peer's parity half of the id space.
    /// </summary>
    private readonly HashSet<ushort> _acceptedInboundIds = [];
    private readonly Lock _acceptedInboundIdsLock = new();

    private int _polling;
    private int _disposed;

    public WebRtcDirectSctpMuxer(RTCSctpTransport transport, bool isDtlsClient)
    {
        _transport = transport;
        // Captured once: unsubscribing in DisposeAsync has to reach the same instance, and this is
        // the only association the transport ever creates.
        _association = transport.RTCSctpAssociation;
        _isDtlsClient = isDtlsClient;

        // RFC 8832 section 6: "If the side is acting as the DTLS client, it MUST choose an even
        // stream identifier; if the side is acting as the DTLS server, it MUST choose an odd one."
        // Zero is even, so it falls in the client's half -- and libp2p reserves it for the Noise
        // handshake channel, which is why the client starts at 2 rather than 0.
        _nextOutboundStreamId = isDtlsClient ? 2 : 1;

        _transport.OnStateChanged += OnSctpStateChanged;
        _association.OnDataChannelData += OnDataChannelData;
        _association.OnDataChannelOpened += OnDataChannelOpened;
        _association.OnNewDataChannel += OnNewDataChannel;

        _bufferedAmountPoll = new Timer(_ => PollBufferedAmount(), null, BufferedAmountPollInterval, BufferedAmountPollInterval);

        // OnStateChanged only fires on a transition. Constructed on a transport that is already
        // connected, nothing would ever complete _connected and OpenStreamAsync would hang.
        if (_transport.state == RTCSctpTransportState.Connected)
        {
            _connected.TrySetResult();
        }
    }

    public IAsyncEnumerable<SctpWebRtcDataChannel> InboundChannels => _inboundChannels.Reader.ReadAllAsync();

    public Task Connected => _connected.Task;

    public Task Completed => _completed.Task;

    /// <summary>
    /// The channel both sides agree on without DCEP, used for the Noise handshake on
    /// <see cref="NoiseStreamId"/>.
    /// </summary>
    public SctpWebRtcDataChannel GetNegotiatedChannel(ushort streamId)
    {
        if (!TryRegisterChannel(streamId, out SctpWebRtcDataChannel? channel))
        {
            throw new InvalidOperationException($"Cannot open stream {streamId}: {MaxConcurrentChannels} channels are already open.");
        }

        if (_transport.state == RTCSctpTransportState.Connected)
        {
            channel.MarkOpen();
        }

        return channel;
    }

    public async Task<SctpWebRtcDataChannel> OpenStreamAsync(CancellationToken token)
    {
        await _connected.Task.WaitAsync(token);

        // Checked before allocating, because a stream id is never reused: letting a rejected open
        // burn one would let a peer that keeps us at the limit drain the id space instead.
        if (_channels.Count >= MaxConcurrentChannels)
        {
            throw new InvalidOperationException($"Cannot open a stream: {MaxConcurrentChannels} channels are already open.");
        }

        ushort streamId = NextOutboundStreamId();
        if (!TryRegisterChannel(streamId, out SctpWebRtcDataChannel? channel))
        {
            throw new InvalidOperationException($"Cannot open a stream: {MaxConcurrentChannels} channels are already open.");
        }

        try
        {
            _association.SendData(streamId, (uint)DataChannelPayloadProtocols.WebRTC_DCEP, CreateDcepOpenMessage());
            await channel.Opened.WaitAsync(token);
        }
        catch
        {
            // The id stays spent, but the concurrency slot must come back: otherwise a peer that
            // accepts DCEP OPENs and never ACKs wedges stream opening on a live connection.
            channel.Close();
            throw;
        }

        return channel;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _transport.OnStateChanged -= OnSctpStateChanged;
        _association.OnDataChannelData -= OnDataChannelData;
        _association.OnDataChannelOpened -= OnDataChannelOpened;
        _association.OnNewDataChannel -= OnNewDataChannel;
        _bufferedAmountPoll.Dispose();

        _inboundChannels.Writer.TryComplete();
        _completed.TrySetResult();
        FailConnect("The SCTP muxer was disposed before the transport connected.");

        try
        {
            _transport.Close();
        }
        catch
        {
            // Closing an association that never established, or already aborted, is not a failure
            // of disposal.
        }

        // Snapshot: Close removes the channel from the dictionary.
        foreach (SctpWebRtcDataChannel channel in _channels.Values.ToArray())
        {
            channel.Close();
        }

        _channels.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// DCEP OPEN for a reliable, ordered channel with no label and no subprotocol: a libp2p stream
    /// carries its own protocol negotiation, so there is nothing for those fields to say. Twelve
    /// bytes, fixed for every stream we open.
    /// </summary>
    internal static byte[] CreateDcepOpenMessage()
    {
        DataChannelOpenMessage open = new()
        {
            MessageType = (byte)DataChannelMessageTypes.OPEN,
            ChannelType = (byte)DataChannelTypes.DATA_CHANNEL_RELIABLE,
            Label = string.Empty,
            Protocol = string.Empty,
        };

        return open.GetBytes();
    }

    /// <summary>
    /// Next id from this side's half of the space. Ids are never reused: RFC 8832 section 6 allows
    /// a stream to be reopened only once both directions have been reset, and SIPSorcery implements
    /// no RFC 6525 RE-CONFIG chunk to reset them with, so a recycled id risks delivering a late
    /// chunk of the previous stream into the new one. The price is a hard ceiling of 32767 streams
    /// opened per association (32768 as the DTLS server, which does not give up id 0), after which
    /// the caller has to open a new connection; <see cref="MaxConcurrentChannels"/> is what bounds
    /// the live ones.
    /// </summary>
    internal ushort NextOutboundStreamId()
    {
        lock (_streamIdLock)
        {
            if (_nextOutboundStreamId > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "The SCTP stream id space for this side of the association is exhausted; stream ids are never reused.");
            }

            ushort streamId = (ushort)_nextOutboundStreamId;
            _nextOutboundStreamId += 2;
            return streamId;
        }
    }

    /// <summary>
    /// Single gate for both directions: returns the existing channel for a stream, or creates one
    /// while <see cref="MaxConcurrentChannels"/> allows it.
    /// </summary>
    internal bool TryRegisterChannel(ushort streamId, out SctpWebRtcDataChannel channel)
    {
        while (true)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                channel = null!;
                return false;
            }

            if (_channels.TryGetValue(streamId, out SctpWebRtcDataChannel? existing))
            {
                channel = existing;
                return true;
            }

            if (_channels.Count >= MaxConcurrentChannels)
            {
                channel = null!;
                return false;
            }

            SctpWebRtcDataChannel created = new(_association, streamId);

            // Freeing the slot on close is what makes the limit a concurrency limit rather than a
            // lifetime one. The id itself stays spent, so nothing can be registered under it again.
            created.Closed += () => ((ICollection<KeyValuePair<ushort, SctpWebRtcDataChannel>>)_channels)
                .Remove(new KeyValuePair<ushort, SctpWebRtcDataChannel>(streamId, created));

            if (_channels.TryAdd(streamId, created))
            {
                channel = created;
                return true;
            }

            created.Close();
        }
    }

    private void PollBufferedAmount()
    {
        // Timer ticks can overlap if a poll runs long; one at a time is enough, and a skipped tick
        // costs at most one extra interval of latency for a parked writer.
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            // SendBufferedAmount is association-wide and sums the whole send queue on each read,
            // so it is read once per tick rather than once per channel.
            long buffered = (long)_association.SendBufferedAmount;
            foreach (SctpWebRtcDataChannel channel in _channels.Values)
            {
                try
                {
                    channel.PollBufferedAmount(buffered);
                }
                catch
                {
                    // Per channel: one throwing subscriber must not strand the writers on every
                    // channel after it in the iteration.
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void OnSctpStateChanged(RTCSctpTransportState state)
    {
        if (state == RTCSctpTransportState.Connected)
        {
            _connected.TrySetResult();

            // The negotiated handshake channel has no DCEP exchange to wait for, so the transport
            // coming up is the only thing that can open it.
            if (_channels.TryGetValue(NoiseStreamId, out SctpWebRtcDataChannel? noise))
            {
                noise.MarkOpen();
            }
        }
        else if (state == RTCSctpTransportState.Closed)
        {
            FailConnect("The SCTP transport closed before connecting.");
            _inboundChannels.Writer.TryComplete();
            _completed.TrySetResult();

            foreach (SctpWebRtcDataChannel channel in _channels.Values.ToArray())
            {
                channel.Close();
            }
        }
    }

    private void OnDataChannelData(SctpDataFrame frame)
    {
        if (_channels.TryGetValue(frame.StreamID, out SctpWebRtcDataChannel? channel))
        {
            channel.Receive(frame);
        }
    }

    private void OnDataChannelOpened(ushort streamId)
    {
        if (_channels.TryGetValue(streamId, out SctpWebRtcDataChannel? channel))
        {
            channel.MarkOpen();
        }
    }

    private void OnNewDataChannel(
        ushort streamId,
        DataChannelTypes type,
        ushort priority,
        uint reliability,
        string label,
        string protocol)
    {
        if (!TryAcceptInboundChannel(streamId, type, out SctpWebRtcDataChannel? channel))
        {
            // The refusal is to send no ACK. With no RE-CONFIG chunk available there is no reset
            // to send either, so withholding the ACK is the whole of it.
            return;
        }

        _association.SendData(streamId, (uint)DataChannelPayloadProtocols.WebRTC_DCEP, [(byte)DataChannelMessageTypes.ACK]);
        channel.MarkOpen();

        if (!_inboundChannels.Writer.TryWrite(channel))
        {
            channel.Close();
        }
    }

    /// <summary>
    /// Whether to accept a peer's DCEP OPEN. Separated from the event handler because
    /// <c>OnNewDataChannel</c> cannot be raised from a test, so this keeps the decision itself
    /// covered even though the wiring around it is not.
    /// </summary>
    internal bool TryAcceptInboundChannel(ushort streamId, DataChannelTypes type, out SctpWebRtcDataChannel channel)
    {
        channel = null!;

        // RFC 8832 section 6 gives each side its own parity, so an id out of our half would
        // collide with what NextOutboundStreamId is about to hand out. Id 0 is ours either way:
        // it is the negotiated Noise channel, which is never opened with DCEP.
        if (streamId == NoiseStreamId || (streamId % 2 == 1) != _isDtlsClient)
        {
            return false;
        }

        // The framing above is a byte stream of length-prefixed messages, so anything other than
        // ordered and fully reliable delivery would desync the length prefixes rather than merely
        // lose data.
        if (type != DataChannelTypes.DATA_CHANNEL_RELIABLE)
        {
            return false;
        }

        lock (_acceptedInboundIdsLock)
        {
            // A second OPEN for a live id would otherwise return the existing channel through
            // get-or-add and publish it to InboundChannels again, handing one SCTP stream to two
            // independent readers. A reopen after close is refused for the same reason outbound
            // ids are never recycled.
            if (!_acceptedInboundIds.Add(streamId))
            {
                return false;
            }
        }

        if (!TryRegisterChannel(streamId, out channel))
        {
            lock (_acceptedInboundIdsLock)
            {
                // Refused for capacity, not for identity: let the peer retry this id later.
                _acceptedInboundIds.Remove(streamId);
            }

            return false;
        }

        return true;
    }

    private void FailConnect(string reason)
    {
        if (_connected.TrySetException(new InvalidOperationException(reason)))
        {
            // Nothing need be awaiting Connected; observe it rather than let it surface as a
            // TaskScheduler.UnobservedTaskException.
            _ = _connected.Task.Exception;
        }
    }
}
