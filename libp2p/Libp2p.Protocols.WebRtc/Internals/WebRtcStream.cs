// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// A libp2p stream over a single WebRTC data channel: length-prefixed <see cref="Message"/> framing
/// plus the FIN/FIN_ACK half-close handshake, which the spec adds because closing an RTCDataChannel
/// closes both directions at once and drops in-flight data.
/// </summary>
internal sealed class WebRtcStream : IAsyncDisposable
{
    /// <summary>
    /// Stop feeding the channel once two full frames are queued in it, and resume on
    /// <see cref="IWebRtcDataChannel.BufferedAmountLow"/>. Two frames keeps the channel busy while
    /// bounding how much a slow peer can make us hold; it matches go-libp2p's WebRTC transport.
    /// </summary>
    public const int MaxBufferedAmount = 2 * WebRtcFraming.MaxFrameSize;

    /// <summary>
    /// How long <see cref="CloseWritesAsync"/> waits for the FIN_ACK that lets it consider the write
    /// half closed. The spec sets no value, it only permits giving up ("The node MAY close the
    /// datachannel without receiving a FIN_ACK, for example in the case of a timeout"), so this is
    /// the go-libp2p convention rather than a normative limit.
    /// </summary>
    public static readonly TimeSpan DefaultFinAckTimeout = TimeSpan.FromSeconds(10);

    private const int MaxBufferedInboundFrames = 256;

    private readonly IWebRtcDataChannel _channel;
    private readonly TimeSpan _finAckTimeout;
    private readonly Channel<byte[]> _inbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedInboundFrames)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Serializes application writes, and only those, around the congestion wait. There is one
    /// <see cref="_drained"/> slot, so two writers parking on a full send buffer would have the
    /// second overwrite the first's completion source and leave it with nothing left to wake it: the drain releases
    /// only the last registration, and neither a later drain nor <see cref="Fail"/> can find the
    /// one that was replaced. Teardown flags never take this, which is what keeps a FIN_ACK from
    /// queueing behind a writer parked on a peer that is not reading.
    /// </summary>
    private readonly SemaphoreSlim _callerSendLock = new(1, 1);

    /// <summary>
    /// Cancelled when the stream dies, so a writer queued on <see cref="_callerSendLock"/> stops
    /// waiting. <c>SemaphoreSlim.Dispose</c> does not complete an existing <c>WaitAsync</c>, so
    /// disposing the semaphore under a queued writer left it pending for ever; the wait has to
    /// observe the stream's lifetime instead.
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _sync = new();
    private readonly Lock _receiveLock = new();
    private readonly TaskCompletionSource _finAck = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writesClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[] _partialFrame = [];
    private TaskCompletionSource? _drained;
    private Exception? _failure;
    private int _closeWritesStarted;
    private bool _finSent;
    private bool _finReceived;
    private bool _finAckReceived;
    private bool _readsStopped;
    private bool _peerStoppedReading;
    private bool _finAckSent;
    private int _detached;

    public WebRtcStream(IWebRtcDataChannel channel, TimeSpan? finAckTimeout = null)
    {
        _channel = channel;
        _finAckTimeout = finAckTimeout ?? DefaultFinAckTimeout;
        _channel.MessageReceived += OnMessage;
        _channel.BufferedAmountLow += ReleaseWriter;
        _channel.Closed += OnChannelClosed;

        // The Closed event may already have fired: an accepted stream can sit queued for the
        // session loop, and the channel can die in the meantime. Subscribing after that point sees
        // nothing, so the state is read once here.
        if (_channel.IsClosed)
        {
            OnChannelClosed();
        }
    }

    /// <summary>
    /// Next payload chunk, or null once the stream has ended. Chunking is a framing artifact: a
    /// caller that needs its original message boundaries has to delimit them itself, as it would on
    /// any other libp2p stream.
    /// </summary>
    /// <summary>
    /// Raised once when the stream dies for a reason that is not an orderly half-close: a peer
    /// RESET_STREAM, the data channel going away underneath, or local disposal. An orderly FIN does
    /// NOT raise it -- that path completes the inbound queue normally and surfaces as an end of
    /// stream, which a reader is entitled to treat as a complete message.
    /// </summary>
    public event Action? Terminated;

    /// <summary>Whether <see cref="Terminated"/> has already fired, for a late subscriber.</summary>
    public bool IsTerminated { get { lock (_sync) { return _failure is not null; } } }

    /// <summary>The SCTP stream this one runs on; see <see cref="IWebRtcDataChannel.StreamId"/>.</summary>
    public ushort StreamId => _channel.StreamId;

    public async ValueTask<byte[]?> ReadAsync(CancellationToken token = default)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(token);
        }
        catch (ChannelClosedException ex) when (ex.InnerException is null)
        {
            return null;
        }
        catch (ChannelClosedException ex)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException!);
            throw;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        // Checked up front so a zero-length write still reports a dead stream rather than
        // silently succeeding by skipping the loop.
        ThrowIfSendingIsOver(callerData: true);

        int maxChunk = WebRtcFraming.MaxPayloadSize(false);
        for (int offset = 0; offset < data.Length; offset += maxChunk)
        {
            ReadOnlyMemory<byte> chunk = data.Slice(offset, Math.Min(maxChunk, data.Length - offset));
            await SendFrameAsync(WebRtcFraming.Encode(null, chunk), callerData: true, token);
        }
    }

    /// <summary>
    /// "When a node wishes to close a stream for writing, it MUST send a message with the FIN flag
    /// set" and "SHOULD only consider its write-half closed once it has received a FIN_ACK".
    /// Idempotent: later calls hand back the first call's task.
    /// </summary>
    public Task CloseWritesAsync(CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref _closeWritesStarted, 1) == 0)
        {
            _ = RunCloseWritesAsync(token);
        }

        return _writesClosed.Task;
    }

    /// <summary>
    /// Sends STOP_SENDING and discards further inbound payload. Flags keep being processed: "If a
    /// node has previously sent a STOP_SENDING flag to the remote node, it MUST continue to act on
    /// any flags present in received messages in order to successfully process an incoming FIN_ACK."
    /// </summary>
    public async ValueTask StopReadingAsync(CancellationToken token = default)
    {
        lock (_sync)
        {
            if (_readsStopped)
            {
                return;
            }

            _readsStopped = true;
        }

        _inbound.Writer.TryComplete();
        await SendFrameAsync(WebRtcFraming.Encode(Message.Types.Flag.StopSending, null), callerData: false, token);
    }

    public async ValueTask ResetAsync(CancellationToken token = default)
    {
        // Fail before sending: this releases a writer parked on backpressure, which would
        // otherwise hold the send lock and make the reset queue behind the very write it exists
        // to abort.
        Fail(new IOException("The stream was reset locally."));

        try
        {
            await SendRawAsync(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null), token);
        }
        catch (Exception)
        {
            // A reset tears the stream down whether or not the flag can still reach the peer.
        }
    }

    private async Task RunCloseWritesAsync(CancellationToken token)
    {
        try
        {
            lock (_sync)
            {
                _finSent = true;
            }

            await SendFrameAsync(WebRtcFraming.Encode(Message.Types.Flag.Fin, null), callerData: false, token);
            await _finAck.Task.WaitAsync(_finAckTimeout, token);
            ThrowIfSendingIsOver(callerData: false);
            _writesClosed.TrySetResult();
        }
        catch (TimeoutException)
        {
            // Unacknowledged, so delivery of what we sent is no longer guaranteed, but the caller
            // must not be pinned on a peer that never acks. "The node MAY close the datachannel
            // without receiving a FIN_ACK, for example in the case of a timeout" -- so close it
            // here, since MaybeCloseChannel never will without the ack.
            _writesClosed.TrySetResult();
            _channel.Close();
        }
        catch (Exception ex)
        {
            _writesClosed.TrySetException(ex);
        }

        try
        {
            MaybeCloseChannel();
        }
        catch (Exception ex)
        {
            // This runs on a detached task; an adapter whose Close() throws must not reach
            // TaskScheduler.UnobservedTaskException.
            Fail(ex);
        }
    }

    private async ValueTask SendFrameAsync(byte[] frame, bool callerData, CancellationToken token)
    {
        // Only caller data waits for capacity, and it waits BEFORE taking the send lock. Flag
        // frames are a few bytes and bounded in number per stream, so gating them on congestion
        // would withhold the FIN_ACK a peer is blocked on; and waiting inside the lock would let a
        // parked writer block every teardown path behind it.
        if (!callerData)
        {
            ThrowIfSendingIsOver(callerData: false);
            await SendRawAsync(frame, token);
            return;
        }

        // Held across the wait AND the send, so the next application write does not register its
        // own congestion waiter while this one is still parked on it. Linked to the stream's
        // lifetime so teardown reaches a writer queued here, not only the one holding it.
        using CancellationTokenSource queued = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            await _callerSendLock.WaitAsync(queued.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The stream died while this writer was queued. Report that, not a bare cancellation.
            ThrowIfSendingIsOver(callerData: true);
            throw;
        }

        try
        {
            await WaitForCapacityAsync(token);
            await SendRawAsync(frame, token);
        }
        finally
        {
            _callerSendLock.Release();
        }
    }

    /// <summary>
    /// Sends with no state or capacity checks, so teardown can still emit its flag after
    /// <see cref="Fail"/> has marked the stream dead.
    /// </summary>
    private async ValueTask SendRawAsync(byte[] frame, CancellationToken token)
    {
        await _sendLock.WaitAsync(token);
        try
        {
            await _channel.SendAsync(frame, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async ValueTask WaitForCapacityAsync(CancellationToken token)
    {
        while (true)
        {
            ThrowIfSendingIsOver(callerData: true);
            if (_channel.BufferedAmount < MaxBufferedAmount)
            {
                return;
            }

            TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _drained, drained);

            // Both re-checks happen AFTER publishing the waiter, and both are lost wake-ups
            // otherwise. A drain in between is the obvious one. The other is a terminal send event
            // -- a close, or a STOP_SENDING -- landing after the check at the top of this loop:
            // its ReleaseWriter finds no waiter, and registering one afterwards means waiting for a
            // notification that has already been and gone. STOP_SENDING is why a state re-read is
            // needed rather than just cancellation; it ends this side's writing without ending the
            // stream, so no token fires for it.
            ThrowIfSendingIsOver(callerData: true);
            if (_channel.BufferedAmount < MaxBufferedAmount)
            {
                Interlocked.CompareExchange(ref _drained, null, drained);
                return;
            }

            // Linked to the stream's lifetime as well, so a close or disposal does not leave this
            // write parked waiting for a caller token that may never fire.
            using CancellationTokenSource waiting =
                CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            try
            {
                await drained.Task.WaitAsync(waiting.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Report what actually happened to the stream rather than a bare cancellation.
                ThrowIfSendingIsOver(callerData: true);
                throw;
            }
        }
    }

    private void ThrowIfSendingIsOver(bool callerData)
    {
        lock (_sync)
        {
            if (_failure is not null)
            {
                ExceptionDispatchInfo.Throw(_failure);
            }

            if (!callerData)
            {
                return;
            }

            if (_finSent)
            {
                throw new InvalidOperationException("The write half of the stream is closed.");
            }

            if (_peerStoppedReading)
            {
                throw new IOException("The peer sent STOP_SENDING and is discarding further data.");
            }
        }
    }

    private void OnMessage(byte[] message)
    {
        List<Message> decoded = [];
        try
        {
            // Only the reassembly buffer needs the lock: a data channel delivers messages one at a
            // time, and decoded messages are handled outside it so nothing awaits under a lock.
            lock (_receiveLock)
            {
                byte[] buffer = _partialFrame.Length == 0 ? message : [.. _partialFrame, .. message];
                int offset = 0;
                while (WebRtcFraming.TryDecode(buffer.AsSpan(offset), out Message? frame, out int consumed))
                {
                    decoded.Add(frame);
                    offset += consumed;
                }

                _partialFrame = offset == buffer.Length ? [] : buffer[offset..];
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }

        foreach (Message frame in decoded)
        {
            Handle(frame);
        }
    }

    private void Handle(Message message)
    {
        // Payload before flag: a single message may carry both, and those bytes precede the end of
        // the stream the flag announces.
        if (message.HasMessage_ && message.Message_.Length != 0)
        {
            bool overflowed = false;
            lock (_sync)
            {
                // Decided and written under one lock: taken apart, a concurrent StopReadingAsync
                // can complete the writer between the two and the resulting TryWrite failure is
                // misreported as an overflow.
                if (!_readsStopped && !_finReceived && _failure is null)
                {
                    overflowed = !_inbound.Writer.TryWrite(message.Message_.ToByteArray());
                }
            }

            if (overflowed)
            {
                Fail(new IOException($"Inbound buffer overflow: more than {MaxBufferedInboundFrames} frames are unread."));
                return;
            }
        }

        if (!message.HasFlag)
        {
            return;
        }

        switch (message.Flag)
        {
            case Message.Types.Flag.Fin:
                bool firstFin;
                lock (_sync)
                {
                    firstFin = !_finReceived;
                    _finReceived = true;
                }

                // Answer only the first one. The spec does not forbid a peer retransmitting FIN,
                // so a repeat is ignored rather than treated as a violation -- but replying to
                // each would let a peer drive one detached task and one send-lock acquisition per
                // frame it chooses to send.
                if (!firstFin)
                {
                    break;
                }

                _inbound.Writer.TryComplete();
                // "If a FIN flag is received the node SHOULD respond with a FIN_ACK."
                _ = SendFinAckAsync();
                break;

            case Message.Types.Flag.FinAck:
                lock (_sync)
                {
                    _finAckReceived = true;
                }

                _finAck.TrySetResult();
                MaybeCloseChannel();
                break;

            case Message.Types.Flag.StopSending:
                // Unlike QUIC, a libp2p WebRTC endpoint "SHOULD NOT send a RESET_STREAM frame in
                // reply", and the read half stays up so later flags are still acted on.
                bool awaitingFinAck;
                lock (_sync)
                {
                    _peerStoppedReading = true;
                    awaitingFinAck = _finSent;
                }

                // A peer that has stopped reading will never ack our FIN, so release the waiter
                // instead of letting CloseWritesAsync burn its whole timeout.
                if (awaitingFinAck)
                {
                    _finAck.TrySetResult();
                }

                ReleaseWriter();
                break;

            case Message.Types.Flag.ResetStream:
                Fail(new IOException("The stream was reset by the peer."));
                break;
        }
    }

    /// <summary>
    /// The FIN_ACK reply originates in the data channel callback, which cannot await. Failures are
    /// routed into <see cref="Fail"/> so they surface on the next read or write instead of being
    /// lost on a detached task.
    /// </summary>
    private async Task SendFinAckAsync()
    {
        try
        {
            await SendFrameAsync(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null), callerData: false, CancellationToken.None);
            lock (_sync)
            {
                _finAckSent = true;
            }

            MaybeCloseChannel();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void MaybeCloseChannel()
    {
        lock (_sync)
        {
            // "When a FIN_ACK and a FIN have been received, the node may close the datachannel."
            // A FIN_ACK answers a FIN we sent, so _finSent is implied; and the FIN_ACK we owe the
            // peer must have left before we tear the channel down under it.
            if (!_finSent || !_finReceived || !_finAckReceived || !_finAckSent)
            {
                return;
            }
        }

        _channel.Close();
    }

    private void OnChannelClosed()
    {
        bool ended;
        lock (_sync)
        {
            // Both halves, not just the peer's. A received FIN ends reading; if the transport then
            // disappears while this side still has an open write half, the stream has lost its
            // channel rather than finished, and a caller awaiting it must be told.
            ended = _failure is not null || (_finReceived && _finSent);
        }

        if (!ended)
        {
            // Completing cleanly here would make a dropped connection indistinguishable from a
            // peer that half-closed, turning a truncated transfer into silent data loss.
            Fail(new IOException("The data channel closed before the stream ended."));
            return;
        }

        _inbound.Writer.TryComplete();
        _finAck.TrySetResult();
        ReleaseWriter();
    }

    private void ReleaseWriter() => Interlocked.Exchange(ref _drained, null)?.TrySetResult();

    /// <summary>
    /// Unsubscribes from the channel: without this the channel holds every stream it has ever
    /// wrapped, and a late callback mutates state the owner considers finished.
    /// </summary>
    private void Detach()
    {
        if (Interlocked.Exchange(ref _detached, 1) == 1)
        {
            return;
        }

        _channel.MessageReceived -= OnMessage;
        _channel.BufferedAmountLow -= ReleaseWriter;
        _channel.Closed -= OnChannelClosed;
    }

    public async ValueTask DisposeAsync()
    {
        Detach();
        Fail(new IOException("The stream was disposed."));
        await Task.CompletedTask;
        // Deliberately NOT disposing _sendLock/_callerSendLock. Fail() above cancels _lifetime,
        // which is what releases anyone queued on them, and disposing a SemaphoreSlim does not
        // complete an existing WaitAsync -- it just makes the waiter's eventual Release throw.
        // Neither semaphore's AvailableWaitHandle is ever touched, so there is nothing to leak.
        _lifetime.Dispose();
    }

    private void Fail(Exception error)
    {
        lock (_sync)
        {
            if (_failure is not null)
            {
                return;
            }

            _failure = error;
        }

        _inbound.Writer.TryComplete(error);

        // "The receiver MAY discard any data that it already received on that stream": dropping it
        // keeps the failure immediate instead of hiding behind whatever was buffered. Drained
        // first, because a bounded channel only faults Completion once its queue is empty, so
        // observing the exception before this would observe nothing.
        while (_inbound.Reader.TryRead(out _))
        {
        }

        // Nothing awaits Completion, so observe it here rather than let it reach
        // TaskScheduler.UnobservedTaskException; readers get the failure from ReadAsync.
        _ = _inbound.Reader.Completion.Exception;
        _finAck.TrySetResult();
        ReleaseWriter();

        // Before closing the channel: a writer queued on the application send lock is woken by
        // this and nothing else.
        _lifetime.Cancel();
        _channel.Close();

        // Last, so a subscriber observing this sees the stream already in its final state.
        Terminated?.Invoke();
    }
}
