// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// A libp2p channel over a single WebRTC data channel: length-prefixed <see cref="Message"/> framing
/// plus the FIN/FIN_ACK half-close handshake, which the spec adds because closing an RTCDataChannel
/// closes both directions at once and drops in-flight data.
/// </summary>
internal sealed class WebRtcChannel : IChannel, IAsyncDisposable
{
    /// <summary>
    /// Stop feeding the channel once two full frames are queued in it, and resume on
    /// <see cref="IWebRtcDataChannel.BufferedAmountLow"/>. Two frames keeps the channel busy while
    /// bounding how much a slow peer can make us hold; it matches go-libp2p's WebRTC transport.
    /// </summary>
    public const int MaxBufferedAmount = 2 * WebRtcFraming.MaxFrameSize;

    /// <summary>
    /// How long <see cref="WriteEofAsync"/> waits for the FIN_ACK that lets it consider the write
    /// half closed. The spec sets no value, it only permits giving up ("The node MAY close the
    /// datachannel without receiving a FIN_ACK, for example in the case of a timeout"), so this is
    /// the go-libp2p convention rather than a normative limit.
    /// </summary>
    public static readonly TimeSpan DefaultFinAckTimeout = TimeSpan.FromSeconds(10);

    private const int MaxBufferedInboundFrames = 256;

    private readonly IWebRtcDataChannel _channel;
    private readonly TimeSpan _finAckTimeout;
    private readonly Nethermind.Libp2p.Core.Channel _received = new();
    private readonly IChannel _delivery;
    private readonly Task _deliveryTask;
    private readonly Channel<byte[]> _inbound = System.Threading.Channels.Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxBufferedInboundFrames)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>
    /// Serializes complete application writes and their EOF around the congestion wait. There is one
    /// <see cref="_drained"/> slot, so two writers parking on a full send buffer would have the
    /// second overwrite the first's completion source and leave it with nothing left to wake it: the drain releases
    /// only the last registration, and neither a later drain nor <see cref="Fail"/> can find the
    /// one that was replaced. Control replies and aborts never take this, which keeps a FIN_ACK from
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

    private byte[] _partialFrame = [];
    private TaskCompletionSource? _drained;
    private Exception? _failure;
    private bool _closing;
    private bool _finSent;
    private bool _finReceived;
    private bool _finAckReceived;
    private bool _readsStopped;
    private bool _peerStoppedReading;
    private bool _finAckSent;
    private int _detached;

    public WebRtcChannel(IWebRtcDataChannel channel, TimeSpan? finAckTimeout = null)
    {
        _channel = channel;
        _finAckTimeout = finAckTimeout ?? DefaultFinAckTimeout;
        _delivery = _received.Reverse;
        _deliveryTask = DeliverIncomingAsync();
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

    public TaskAwaiter GetAwaiter() => _received.GetAwaiter();

    internal ushort StreamId => _channel.StreamId;

    public ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default) => _received.ReadAsync(length, blockingMode, token);

    private async Task DeliverIncomingAsync()
    {
        try
        {
            // Application reads may stall. Only this pump waits for them, so the receive callback
            // can still process FIN_ACK, STOP_SENDING and RESET_STREAM behind unread payload.
            await foreach (byte[] payload in _inbound.Reader.ReadAllAsync(_lifetime.Token))
            {
                // Core.Channel may return slices of this array to a reader after WriteAsync
                // completes. Each payload owns its array; it must not be pooled or reused here.
                if (await _delivery.WriteAsync(new ReadOnlySequence<byte>(payload), _lifetime.Token) != IOResult.Ok)
                {
                    return;
                }
            }

            await _delivery.WriteEofAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            while (_inbound.Reader.TryRead(out _))
            {
            }
        }
    }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        try
        {
            ThrowIfSendingIsOver(callerData: true);
            using CancellationTokenSource queued = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _callerSendLock.WaitAsync(queued.Token);
            try
            {
                ThrowIfSendingIsOver(callerData: true);
                int maxChunk = WebRtcFraming.MaxPayloadSize(false);
                CancellationToken sendingToken = queued.Token;
                while (!bytes.IsEmpty)
                {
                    await WaitForCapacityAsync(sendingToken);
                    ReadOnlySequence<byte> chunk = bytes.Slice(0, Math.Min(bytes.Length, maxChunk));
                    await SendRawAsync(WebRtcFraming.Encode(null, chunk.IsSingleSegment ? chunk.First : chunk.ToArray()), sendingToken);
                    bytes = bytes.Slice(chunk.Length);
                    // As in Core.Channel, a partially delivered write owes the rest of its bytes.
                    // Reporting caller cancellation now would invite a retry of the sent prefix.
                    sendingToken = _lifetime.Token;
                }
            }
            finally
            {
                _callerSendLock.Release();
            }

            return IOResult.Ok;
        }
        catch (OperationCanceledException)
        {
            return _lifetime.IsCancellationRequested ? IOResult.Aborted : IOResult.Cancelled;
        }
        catch (InvalidOperationException) when ((_finSent || _closing) && _failure is null)
        {
            return _closing ? IOResult.Aborted : IOResult.Ended;
        }
        catch (IOException) when (_peerStoppedReading && _failure is null)
        {
            return IOResult.Aborted;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return IOResult.Aborted;
        }
    }

    /// <summary>
    /// "When a node wishes to close a stream for writing, it MUST send a message with the FIN flag
    /// set" and "SHOULD only consider its write-half closed once it has received a FIN_ACK".
    /// Sends FIN once; cancellation before it is sent leaves a later caller free to retry.
    /// </summary>
    public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
    {
        try
        {
            using CancellationTokenSource queued = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            await _callerSendLock.WaitAsync(queued.Token);
            try
            {
                ThrowIfSendingIsOver(callerData: false);
                if (!_finSent)
                {
                    lock (_sync)
                    {
                        _finSent = true;
                    }

                    try
                    {
                        await SendFlagAsync(Message.Types.Flag.Fin, queued.Token);
                    }
                    catch
                    {
                        lock (_sync)
                        {
                            _finSent = false;
                        }

                        throw;
                    }
                }
            }
            finally
            {
                _callerSendLock.Release();
            }

            try
            {
                // FIN commits the write half. Caller cancellation must not abandon its ACK and
                // leave Core completion pending after the peer has acknowledged the final bytes.
                await _finAck.Task.WaitAsync(_finAckTimeout, _lifetime.Token);
            }
            catch (TimeoutException)
            {
                // The spec permits closing without an ACK after a timeout, but delivery is no
                // longer guaranteed. The channel cannot stay open waiting for that missing ACK.
                _channel.Close();
                await _received.WriteEofAsync();
                return IOResult.Ok;
            }

            ThrowIfSendingIsOver(callerData: false);
            // Core owns read assembly and completion. Its otherwise unused write half ends only
            // after the transport has finished writing; inbound EOF follows all delivered payload.
            await _received.WriteEofAsync();
            MaybeCloseChannel();
            return IOResult.Ok;
        }
        catch (OperationCanceledException)
        {
            return _lifetime.IsCancellationRequested ? IOResult.Aborted : IOResult.Cancelled;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return IOResult.Aborted;
        }
    }

    public async ValueTask CloseAsync()
    {
        // Whole-channel close aborts unfinished writes, as Core.Channel.CloseAsync does. Waiting
        // for their EOF here could otherwise hang forever behind a peer that never drains SCTP.
        bool abort;
        lock (_sync)
        {
            // Concurrent closes share the same FIN handshake without blocking new control frames.
            abort = false;
            if (!_closing && !_finSent)
            {
                abort = !_callerSendLock.Wait(0);
                if (!abort)
                {
                    _callerSendLock.Release();
                }
            }

            _closing = true;
        }

        if (abort)
        {
            await AbortAsync();
            return;
        }

        await WriteEofAsync();
        lock (_sync)
        {
            _readsStopped = true;
        }

        _inbound.Writer.TryComplete();
        await _received.CloseAsync();
    }

    /// <summary>
    /// Sends STOP_SENDING and discards further inbound payload. Flags keep being processed: "If a
    /// node has previously sent a STOP_SENDING flag to the remote node, it MUST continue to act on
    /// any flags present in received messages in order to successfully process an incoming FIN_ACK."
    /// </summary>
    public async ValueTask CloseReadAsync()
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
        try
        {
            await SendFlagAsync(Message.Types.Flag.StopSending, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            await _received.CloseReadAsync();
        }
    }

    public async ValueTask AbortAsync()
    {
        // Fail before sending: this releases a writer parked on backpressure, which would
        // otherwise hold the send lock and make the reset queue behind the very write it exists
        // to abort.
        Fail(new IOException("The stream was reset locally."));

        try
        {
            await SendRawAsync(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null), CancellationToken.None);
        }
        catch (Exception)
        {
            // A reset tears the stream down whether or not the flag can still reach the peer.
        }
    }

    private async ValueTask SendFlagAsync(Message.Types.Flag flag, CancellationToken token)
    {
        // Only caller data waits for capacity, and it waits BEFORE taking the send lock. Flag
        // frames are a few bytes and bounded in number per stream, so gating them on congestion
        // would withhold the FIN_ACK a peer is blocked on; and waiting inside the lock would let a
        // parked writer block every teardown path behind it.
        ThrowIfSendingIsOver(callerData: false);
        await SendRawAsync(WebRtcFraming.Encode(flag, null), token);
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

            if (_finSent || _closing)
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
                // Decided and written under one lock: taken apart, a concurrent CloseReadAsync
                // can complete the writer between the two and the resulting TryWrite failure is
                // misreported as an overflow.
                if (!_readsStopped && !_finReceived && _failure is null)
                {
                    overflowed = !_inbound.Writer.TryWrite(message.Message_.ToByteArray());
                }
            }

            if (overflowed)
            {
                Fail(new IOException($"Inbound buffer overflow: more than {MaxBufferedInboundFrames} frames are queued."));
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
                // instead of letting WriteEofAsync burn its whole timeout.
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
            await SendFlagAsync(Message.Types.Flag.FinAck, CancellationToken.None);
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
        await _deliveryTask;
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

        _inbound.Writer.TryComplete();
        _finAck.TrySetResult();
        ReleaseWriter();

        // Before closing the channel: a writer queued on the application send lock is woken by
        // this and nothing else.
        _lifetime.Cancel();
        try
        {
            _channel.Close();
        }
        finally
        {
            // Last: Core completion can synchronously invoke a caller that disposes this channel.
            // Abort also wakes a delivery pump blocked on unread payload and discards that payload.
            _ = _received.AbortAsync();
        }
    }
}
