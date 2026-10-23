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
        public volatile bool DiscardIncoming;
        public int BufferedBytes { get; set; }
    }

    private sealed record OutgoingFrame(byte[] Bytes, StreamState? Stream = null, TaskCompletionSource<IOResult>? Completion = null);

    private sealed class Connection(IChannel transport, IConnectionContext context, ILogger? logger)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<(ulong Id, bool Local), StreamState> _streams = [];
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<OutgoingFrame> _outgoing = System.Threading.Channels.Channel.CreateBounded<OutgoingFrame>(new BoundedChannelOptions(MaxQueuedFrames)
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        private INewSessionContext? _session;
        private CancellationTokenRegistration _sessionClosed;
        private Task _dialRequests = Task.CompletedTask;
        private ulong _nextId;
        private int _bufferedBytes;
        private bool _closing;
        private readonly int _outgoingFrameLength = context.State.PreferredWriteSize is int preferred && preferred > 12
            ? Math.Min(MaxOutgoingFrameLength, preferred - 12)
            : MaxOutgoingFrameLength;

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
                    lock (_gate)
                    {
                        if (_streams.ContainsKey((frame.StreamId, false)))
                            throw new InvalidDataException("Duplicate Mplex stream.");
                        if (!_closing && _streams.Count < MaxStreams)
                            AddStream(frame.StreamId, false, new UpgradeOptions());
                        else
                            SendControl(frame.StreamId, MplexFlag.ResetReceiver);
                    }
                    continue;
                }

                bool local = ((byte)frame.Flag & 1) != 0;
                StreamState? stream;
                lock (_gate) _streams.TryGetValue((frame.StreamId, local), out stream);
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
                            SendControl(stream.Id, stream.Local ? MplexFlag.ResetInitiator : MplexFlag.ResetReceiver);
                    }
                    if (up is not null) await up.AbortAsync();
                }

                stream.Incoming.Writer.TryComplete();
                try { await transfer; }
                catch (Exception) { /* The owning stream has already reported the failure and aborted both pumps. */ }
                while (stream.Incoming.Reader.TryRead(out byte[]? payload)) ReleaseBytes(stream, payload.Length);
                lock (_gate)
                {
                    stream.Retired = true;
                    stream.Stop.Dispose();
                    _streams.Remove((stream.Id, stream.Local));
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
            IOResult result = await completion.Task.WaitAsync(stream.Stop.Token);
            if (result != IOResult.Ok) throw new ChannelAbortedException();
        }

        // The shared reader must never wait on an application or a saturated writer queue.
        private void SendControl(ulong id, MplexFlag flag)
        {
            if (!_outgoing.Writer.TryWrite(new(new MplexFrame(id, flag, 0).Encode(default))))
                _stop.Cancel();
        }

        private async Task WriteFramesAsync()
        {
            try
            {
                await foreach (OutgoingFrame frame in _outgoing.Reader.ReadAllAsync(_stop.Token))
                {
                    IOResult result = IOResult.Aborted;
                    try
                    {
                        if (frame.Stream?.Stop.IsCancellationRequested != true)
                            result = await transport.WriteAsync(new ReadOnlySequence<byte>(frame.Bytes), _stop.Token);
                        if (result != IOResult.Ok && frame.Stream?.Stop.IsCancellationRequested != true)
                            throw new ChannelAbortedException();
                    }
                    finally { frame.Completion?.TrySetResult(result); }
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
                while (_outgoing.Reader.TryRead(out OutgoingFrame? frame)) frame.Completion?.TrySetResult(IOResult.Aborted);
            }
        }
    }
}
