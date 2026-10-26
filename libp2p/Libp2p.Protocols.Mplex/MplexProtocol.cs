// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Mplex;
using System.Buffers;
using System.Threading.Channels;
using ChannelClosedException = Nethermind.Libp2p.Core.Exceptions.ChannelClosedException;

namespace Nethermind.Libp2p.Protocols;

/// <summary>Legacy Mplex interoperability. Prefer Yamux when both peers support it.</summary>
public class MplexProtocol : SymmetricProtocol, IConnectionProtocol
{
    internal const int MaxStreams = 512;
    internal const int MaxBufferedBytesPerStream = 4 * 1024 * 1024;
    internal const int MaxBufferedBytesPerConnection = 16 * 1024 * 1024;
    internal const int MaxQueuedFrames = 64;
    internal const int MaxRejectedResets = 64;
    internal const int MaxResetBurst = 8;
    internal const int MaxOutgoingFrameLength = 16 * 1024;

    private readonly ILogger? _logger;

    public MplexProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
    {
        multiplexerSettings?.Add(this);
        _logger = loggerFactory?.CreateLogger<MplexProtocol>();
    }

    public string Id => "/mplex/6.7.0";

    protected override Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
        => new Connection(channel, context, _logger).RunAsync(isListener);

    private sealed class StreamState(ulong id, bool local, UpgradeOptions options, CancellationToken connectionToken)
    {
        public ulong Id { get; } = id;
        public bool Local { get; } = local;
        public UpgradeOptions Options { get; } = options;
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(connectionToken, options.CancellationToken);
        public Channel<byte[]> Incoming { get; } = System.Threading.Channels.Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedFrames)
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        public Task Worker { get; set; } = Task.CompletedTask;
        public bool ReceiveClosed { get; set; }
        public bool RemoteReset { get; set; }
        public bool Retired { get; set; }
        public bool ResetPending { get; set; }
        public volatile bool DiscardIncoming;
        public int BufferedBytes { get; set; }
    }

    private sealed record OutgoingFrame(byte[] Bytes, StreamState Stream, TaskCompletionSource<IOResult> Completion);

    private sealed record ResetFrame(byte[] Bytes, StreamState? Stream = null);

    internal sealed class Connection(IChannel transport, IConnectionContext context, ILogger? logger)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<(ulong Id, bool Local), StreamState> _streams = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<OutgoingFrame> _outgoing = System.Threading.Channels.Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(MaxQueuedFrames)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        // Accepted resets own their stream's admission reservation; rejected opens own a permit.
        // Both budgets include the frame currently being written.
        private readonly Queue<ResetFrame> _resets = new();
        private readonly SemaphoreSlim _rejectionBudget = new(MaxRejectedResets, MaxRejectedResets);
        private readonly Channel<bool> _writeReady = System.Threading.Channels.Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false,
        });
        private int _pendingResets;
        private int _pendingRejections;
        private bool _rejectionWarningLogged;
        private INewSessionContext? _session;
        private CancellationTokenRegistration _sessionClosed;
        private Task _dialRequests = Task.CompletedTask;
        private ulong _nextId;
        private int _bufferedBytes;
        private bool _closing;
        private readonly int _outgoingFrameLength = context.State.PreferredWriteSize is int preferred && preferred > 12
            ? Math.Min(MaxOutgoingFrameLength, preferred - 12)
            : MaxOutgoingFrameLength;

        internal int QueuedFrameCount => _outgoing.Reader.Count;
        internal int ReservedStreamCount { get { lock (_gate) return _streams.Count; } }
        internal int RetiredStreamCount { get { lock (_gate) return _streams.Values.Count(stream => stream.Retired); } }
        internal int PendingResetCount { get { lock (_gate) return _pendingResets; } }
        internal int PendingRejectionCount { get { lock (_gate) return _pendingRejections; } }
        internal int BufferedByteCount { get { lock (_gate) return _bufferedBytes; } }

        public async Task RunAsync(bool isListener)
        {
            // Acquire the default IChannel completion token only once per connection.
            CancellationTokenRegistration transportClosed = transport.CancellationToken.Register(_stop.Cancel);
            Task writer = WriteFramesAsync();
            try
            {
                if (!isListener) StartSession();
                await ReadFramesAsync();
            }
            catch (Exception exception) when (exception is InvalidDataException or ChannelClosedException or ChannelAbortedException or SessionExistsException || _stop.IsCancellationRequested)
            {
                logger?.LogDebug(exception, "Mplex connection {Context} closed", context.Id);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Mplex connection {Context} failed", context.Id);
            }
            finally
            {
                StreamState[] streams;
                lock (_gate)
                {
                    _closing = true;
                    streams = [.. _streams.Values];
                }

                _stop.Cancel();
                _outgoing.Writer.TryComplete();
                // A token cannot retract a partially consumed channel write. Abort before joining workers.
                await transport.AbortAsync();
                _session?.Dispose();
                await _dialRequests;
                await writer;
                await Task.WhenAll(streams.Select(stream => stream.Worker));
                lock (_gate) _streams.Clear();
                _writeReady.Writer.TryComplete();
                _rejectionBudget.Dispose();
                _sessionClosed.Dispose();
                transportClosed.Dispose();
                _stop.Dispose();
            }
        }

        private void StartSession()
        {
            _session = context.UpgradeToSession();
            _sessionClosed = _session.Token.Register(_stop.Cancel);
            _dialRequests = Task.Run(ReadDialRequests);
        }

        private void ReadDialRequests()
        {
            try
            {
                foreach (UpgradeOptions request in _session!.DialRequests)
                {
                    if (_stop.IsCancellationRequested || request.CancellationToken.IsCancellationRequested)
                    {
                        request.CompletionSource?.TrySetCanceled();
                        continue;
                    }

                    StreamState? stream;
                    lock (_gate)
                    {
                        stream = !_closing && _streams.Count < MaxStreams && _nextId <= MplexFrame.MaxStreamId
                            ? AddStream(_nextId++, true, request)
                            : null;
                    }

                    if (stream is null)
                        request.CompletionSource?.TrySetException(new InvalidOperationException("Mplex stream limit reached."));
                }
            }
            catch (Exception exception) when (_stop.IsCancellationRequested)
            {
                logger?.LogDebug(exception, "Mplex dial queue {Context} closed", context.Id);
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Mplex dial queue {Context} failed", context.Id);
                _stop.Cancel();
            }
        }

        // Called under _gate. The worker invokes application code outside the lock.
        private StreamState AddStream(ulong id, bool local, UpgradeOptions options)
        {
            StreamState stream = new(id, local, options, _stop.Token);
            _streams.Add((id, local), stream);
            stream.Worker = Task.Run(() => RunStreamAsync(stream));
            return stream;
        }

        private async Task ReadFramesAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                MplexFrame frame = await MplexFrame.ReadAsync(transport, _stop.Token);
                if (frame.Flag == MplexFlag.NewStream)
                {
                    await DiscardAsync(frame.Length);
                    // Match the other TCP muxer: defer listener session admission until a valid stream arrives.
                    if (_session is null) StartSession();
                    bool rejected;
                    lock (_gate)
                    {
                        if (_streams.ContainsKey((frame.StreamId, false)))
                            throw new InvalidDataException("Duplicate Mplex stream.");
                        rejected = _closing || _streams.Count >= MaxStreams;
                        if (!rejected) AddStream(frame.StreamId, false, new UpgradeOptions());
                    }
                    // Only the reader waits for rejection capacity, outside the stream lock.
                    if (rejected) await RejectStreamAsync(frame.StreamId);
                    continue;
                }

                bool local = ((byte)frame.Flag & 1) != 0;
                StreamState? stream;
                lock (_gate)
                {
                    _streams.TryGetValue((frame.StreamId, local), out stream);
                    if (stream?.Retired == true) stream = null;
                }
                if (stream is null)
                {
                    // Late frames for retired streams are legal. Never resurrect them.
                    await DiscardAsync(frame.Length);
                    continue;
                }

                if (frame.Flag is MplexFlag.ResetReceiver or MplexFlag.ResetInitiator)
                {
                    lock (_gate)
                    {
                        stream.RemoteReset = true;
                        CancelStream(stream);
                    }
                }
                else if (frame.Flag is MplexFlag.CloseReceiver or MplexFlag.CloseInitiator)
                {
                    lock (_gate) stream.ReceiveClosed = true;
                    stream.Incoming.Writer.TryComplete();
                }
                else
                {
                    bool reserved;
                    lock (_gate)
                    {
                        reserved = !stream.DiscardIncoming && !stream.ReceiveClosed && !stream.Stop.IsCancellationRequested &&
                            stream.BufferedBytes + frame.Length <= MaxBufferedBytesPerStream &&
                            _bufferedBytes + frame.Length <= MaxBufferedBytesPerConnection;
                        if (reserved)
                        {
                            stream.BufferedBytes += frame.Length;
                            _bufferedBytes += frame.Length;
                        }
                        else if (!stream.DiscardIncoming) CancelStream(stream);
                    }

                    if (!reserved)
                    {
                        await DiscardAsync(frame.Length);
                        continue;
                    }

                    if (frame.Length == 0) continue;

                    bool queued = false;
                    try
                    {
                        // IChannel readers may retain data after WriteAsync returns; pooled memory cannot cross it.
                        byte[] payload = (await transport.ReadAsync(frame.Length, token: _stop.Token).OrThrow()).ToArray();
                        queued = stream.Incoming.Writer.TryWrite(payload);
                        if (!queued && !stream.DiscardIncoming) CancelStream(stream);
                    }
                    finally
                    {
                        if (!queued) ReleaseBytes(stream, frame.Length);
                    }
                }
            }
        }

        private async Task DiscardAsync(int length)
        {
            while (length != 0)
            {
                int count = Math.Min(length, MaxOutgoingFrameLength);
                await transport.ReadAsync(count, token: _stop.Token).OrThrow();
                length -= count;
            }
        }

        private void ReleaseBytes(StreamState stream, int count)
        {
            lock (_gate)
            {
                stream.BufferedBytes -= count;
                _bufferedBytes -= count;
            }
        }

        private void CancelStream(StreamState stream)
        {
            lock (_gate)
                if (!stream.Retired) stream.Stop.Cancel();
        }

        private async Task RunStreamAsync(StreamState stream)
        {
            IChannel? up = null;
            Task transfer = Task.CompletedTask;
            bool completed = false;
            try
            {
                stream.Stop.Token.ThrowIfCancellationRequested();
                up = _session!.Upgrade(stream.Options with
                {
                    ModeOverride = stream.Local ? UpgradeModeOverride.Dial : UpgradeModeOverride.Listen,
                });
                TaskCompletionSource channelClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                up.GetAwaiter().OnCompleted(() => channelClosed.TrySetResult());
                transfer = TransferAsync(stream, up);
                await Task.WhenAny(transfer, channelClosed.Task).WaitAsync(stream.Stop.Token);
                if (up.GetAwaiter().IsCompleted)
                {
                    if ((await up.ReadAsync(0, ReadBlockingMode.DoNotWait)).Result == IOResult.Aborted)
                        throw new ChannelAbortedException();
                    StopDelivery(stream);
                }
                await transfer.WaitAsync(stream.Stop.Token);
                // The transfer can win the race with the completion callback after a local abort.
                // Observe the channel itself before treating both completed pumps as a clean close.
                await channelClosed.Task.WaitAsync(stream.Stop.Token);
                if ((await up.ReadAsync(0, ReadBlockingMode.DoNotWait)).Result == IOResult.Aborted)
                    throw new ChannelAbortedException();
                completed = true;
            }
            catch (Exception exception)
            {
                if (stream.Options.CancellationToken.IsCancellationRequested)
                    stream.Options.CompletionSource?.TrySetCanceled(stream.Options.CancellationToken);
                else
                    stream.Options.CompletionSource?.TrySetException(exception);
                if (!_stop.IsCancellationRequested && !stream.Stop.IsCancellationRequested && exception is not ChannelAbortedException and not ChannelClosedException)
                    logger?.LogWarning(exception, "Mplex stream {Stream} failed", stream.Id);
            }
            finally
            {
                if (!completed)
                {
                    lock (_gate)
                    {
                        CancelStream(stream);
                        if (!_stop.IsCancellationRequested && !stream.RemoteReset)
                            QueueReset(stream);
                    }
                    if (up is not null) await up.AbortAsync();
                }

                stream.Incoming.Writer.TryComplete();
                try { await transfer; }
                catch (Exception) { /* The owning stream has already reported the failure and aborted both pumps. */ }
                while (stream.Incoming.Reader.TryRead(out byte[]? payload)) ReleaseBytes(stream, payload.Length);
                lock (_gate)
                {
                    // Cleanup and reset transmission may finish in either order; the last releases admission.
                    stream.Retired = true;
                    stream.Stop.Dispose();
                    if (!stream.ResetPending) _streams.Remove((stream.Id, stream.Local));
                }
            }
        }

        private async Task TransferAsync(StreamState stream, IChannel up)
        {
            if (stream.Local) await SendAsync(stream, MplexFlag.NewStream, default);
            await Task.WhenAll(PumpAsync(DeliverAsync), PumpAsync(ReadApplicationAsync));

            async Task PumpAsync(Func<StreamState, IChannel, Task> pump)
            {
                try { await pump(stream, up); }
                catch
                {
                    CancelStream(stream);
                    throw;
                }
            }
        }

        private async Task DeliverAsync(StreamState stream, IChannel up)
        {
            await foreach (byte[] payload in stream.Incoming.Reader.ReadAllAsync(stream.Stop.Token))
            {
                try
                {
                    if (!stream.DiscardIncoming)
                    {
                        IOResult result = await up.WriteAsync(new ReadOnlySequence<byte>(payload), stream.Stop.Token);
                        stream.Stop.Token.ThrowIfCancellationRequested();
                        if (result is IOResult.Ended or IOResult.Aborted)
                            StopDelivery(stream);
                        else if (result != IOResult.Ok)
                            throw new ChannelAbortedException();
                    }
                }
                finally { ReleaseBytes(stream, payload.Length); }
            }
            if (!stream.DiscardIncoming)
            {
                IOResult result = await up.WriteEofAsync(stream.Stop.Token);
                stream.Stop.Token.ThrowIfCancellationRequested();
                if (result is not IOResult.Ok and not IOResult.Ended and not IOResult.Aborted)
                    throw new ChannelAbortedException();
            }
        }

        private void StopDelivery(StreamState stream)
        {
            // Full application completion (or CloseReadAsync) stops incoming delivery, while
            // the other pump still flushes its pending response and FIN before retirement.
            lock (_gate) stream.DiscardIncoming = true;
            stream.Incoming.Writer.TryComplete();
        }

        private async Task ReadApplicationAsync(StreamState stream, IChannel up)
        {
            await foreach (ReadOnlySequence<byte> bytes in up.ReadAllAsync(stream.Stop.Token))
            {
                ReadOnlySequence<byte> remaining = bytes;
                while (!remaining.IsEmpty)
                {
                    int length = (int)Math.Min(remaining.Length, _outgoingFrameLength);
                    await SendAsync(stream, stream.Local ? MplexFlag.MessageInitiator : MplexFlag.MessageReceiver, remaining.Slice(0, length));
                    remaining = remaining.Slice(length);
                }
            }
            await SendAsync(stream, stream.Local ? MplexFlag.CloseInitiator : MplexFlag.CloseReceiver, default);
        }

        private async Task SendAsync(StreamState stream, MplexFlag flag, ReadOnlySequence<byte> payload)
        {
            TaskCompletionSource<IOResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OutgoingFrame frame = new(new MplexFrame(stream.Id, flag, (int)payload.Length).Encode(payload), stream, completion);
            await _outgoing.Writer.WriteAsync(frame, stream.Stop.Token);
            _writeReady.Writer.TryWrite(true);
            IOResult result = await completion.Task.WaitAsync(stream.Stop.Token);
            if (result != IOResult.Ok) throw new ChannelAbortedException();
        }

        // Called under _gate. The stream already owns a reservation, so this cannot overflow.
        private void QueueReset(StreamState stream)
        {
            if (stream.ResetPending) return;
            stream.ResetPending = true;
            _resets.Enqueue(new(new MplexFrame(stream.Id,
                stream.Local ? MplexFlag.ResetInitiator : MplexFlag.ResetReceiver, 0).Encode(default), stream));
            _pendingResets++;
            _writeReady.Writer.TryWrite(true);
        }

        private async Task RejectStreamAsync(ulong id)
        {
            if (!_rejectionBudget.Wait(0))
            {
                if (!_rejectionWarningLogged && !_stop.IsCancellationRequested)
                {
                    _rejectionWarningLogged = true;
                    logger?.LogWarning("Mplex connection {Context} paused reading at rejected stream {Stream}: {Budget} rejection resets outstanding",
                        context.Id, id, MaxRejectedResets);
                }
                await _rejectionBudget.WaitAsync(_stop.Token);
            }

            bool transferred = false;
            try
            {
                lock (_gate)
                {
                    if (_closing || _stop.IsCancellationRequested) return;
                    _resets.Enqueue(new(new MplexFrame(id, MplexFlag.ResetReceiver, 0).Encode(default)));
                    _pendingResets++;
                    _pendingRejections++;
                    transferred = true;
                }
                _writeReady.Writer.TryWrite(true);
            }
            finally
            {
                if (!transferred) _rejectionBudget.Release();
            }
        }

        private void CompleteReset(ResetFrame frame, bool sent)
        {
            lock (_gate)
            {
                _pendingResets--;
                if (frame.Stream is { } stream)
                {
                    if (sent)
                    {
                        stream.ResetPending = false;
                        if (stream.Retired) _streams.Remove((stream.Id, stream.Local));
                    }
                }
                else
                {
                    _pendingRejections--;
                    _rejectionBudget.Release();
                }
            }
        }

        private async Task WriteFramesAsync()
        {
            try
            {
                int resetBurst = 0;
                while (!_stop.IsCancellationRequested)
                {
                    ResetFrame? reset = null;
                    OutgoingFrame? ordinary = null;
                    lock (_gate)
                    {
                        if (resetBurst < MaxResetBurst) _resets.TryDequeue(out reset);
                    }
                    if (reset is null)
                    {
                        while (_outgoing.Reader.TryRead(out OutgoingFrame? candidate))
                        {
                            if (candidate.Stream.Stop.IsCancellationRequested)
                                candidate.Completion.TrySetResult(IOResult.Aborted);
                            else
                            {
                                ordinary = candidate;
                                break;
                            }
                        }
                        if (ordinary is null)
                            lock (_gate) _resets.TryDequeue(out reset);
                    }

                    if (reset is not null)
                    {
                        bool sent = false;
                        try
                        {
                            IOResult result = await transport.WriteAsync(new ReadOnlySequence<byte>(reset.Bytes), _stop.Token);
                            if (result != IOResult.Ok) throw new ChannelAbortedException();
                            sent = true;
                        }
                        finally { CompleteReset(reset, sent); }
                        resetBurst = Math.Min(resetBurst + 1, MaxResetBurst);
                    }
                    else if (ordinary is not null)
                    {
                        IOResult result = IOResult.Aborted;
                        try
                        {
                            if (!ordinary.Stream.Stop.IsCancellationRequested)
                            {
                                result = await transport.WriteAsync(new ReadOnlySequence<byte>(ordinary.Bytes), _stop.Token);
                                if (result != IOResult.Ok) throw new ChannelAbortedException();
                                resetBurst = 0;
                            }
                        }
                        finally { ordinary.Completion.TrySetResult(result); }
                    }
                    else
                    {
                        // A single coalescing notification covers both queues. Always inspect them before waiting.
                        await _writeReady.Reader.ReadAsync(_stop.Token);
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_stop.IsCancellationRequested)
                {
                    if (exception is ChannelClosedException or ChannelAbortedException)
                        logger?.LogDebug(exception, "Mplex writer {Context} closed", context.Id);
                    else
                        logger?.LogWarning(exception, "Mplex writer {Context} failed", context.Id);
                }
                _stop.Cancel();
                await transport.AbortAsync();
            }
            finally
            {
                _outgoing.Writer.TryComplete();
                while (_outgoing.Reader.TryRead(out OutgoingFrame? frame)) frame.Completion.TrySetResult(IOResult.Aborted);
                lock (_gate)
                    while (_resets.TryDequeue(out ResetFrame? reset)) CompleteReset(reset, false);
            }
        }
    }
}
