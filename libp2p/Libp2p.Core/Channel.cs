// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core.Metrics;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]
[assembly: InternalsVisibleTo("Libp2p.Protocols.Pubsub.Tests")]

namespace Nethermind.Libp2p.Core;

public class Channel : IChannel
{
    private IChannel? _reversedChannel;
    private ReaderWriter _reader;
    private ReaderWriter _writer;
    private TaskCompletionSource Completion = new();

    public Channel()
    {
        _reader = new ReaderWriter(this);
        _writer = new ReaderWriter(this);
    }

    private Channel(ReaderWriter reader, ReaderWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public IChannel Reverse
    {
        get => _reversedChannel ??= new Channel((ReaderWriter)Writer, (ReaderWriter)Reader)
        {
            _reversedChannel = this,
            Completion = Completion
        };
    }

    public IReader Reader { get => _reader; }
    public IWriter Writer { get => _writer; }


    public ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        return Reader.ReadAsync(length, blockingMode, token);
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        return Writer.WriteAsync(bytes, token);
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Writer.WriteEofAsync(token);

    public TaskAwaiter GetAwaiter() => Completion.Task.GetAwaiter();

    public ValueTask CloseAsync()
    {
        bool aborted = _reader.MarkClosed() | _writer.MarkClosed();
        if (aborted)
        {
            _reader.MarkAborted();
        }
        _reader.CompleteAbort();
        _writer.CompleteAbort();
        Completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    private void TryComplete()
    {
        if (_reader._eow && _writer._eow)
        {
            Completion.TrySetResult();
        }
    }


    internal class ReaderWriter : IReader, IWriter
    {
        private const int Closed = 1;
        private const int PendingWrite = 2;
        private const int PendingRead = 4;

        internal protected ReaderWriter(Channel tryComplete)
        {
            _externalCompletionMonitor = tryComplete;
        }

        public ReaderWriter()
        {
        }

        private ReadOnlySequence<byte> _bytes;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly SemaphoreSlim _canRead = new(0, 1);
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private readonly CancellationTokenSource _closed = new();
        private readonly Channel? _externalCompletionMonitor;
        internal volatile bool _eow = false;
        private int _aborted;
        private int _state;

        private ReadResult ClosedReadResult => Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Ended;
        private IOResult ClosedIoResult => Volatile.Read(ref _aborted) != 0 ? IOResult.Aborted : IOResult.Ended;

        internal bool MarkClosed()
        {
            int previous = Interlocked.Or(ref _state, Closed);
            return (previous & (PendingWrite | PendingRead)) != 0;
        }

        internal void MarkAborted()
        {
            if (_externalCompletionMonitor is { } channel)
            {
                Interlocked.Exchange(ref channel._reader._aborted, 1);
                Interlocked.Exchange(ref channel._writer._aborted, 1);
            }
            else
            {
                Interlocked.Exchange(ref _aborted, 1);
            }
        }

        internal void CompleteAbort()
        {
            _eow = true;
            _closed.Cancel();
            _externalCompletionMonitor?.TryComplete();
        }

        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            using CancellationTokenSource? linked = LinkToken(token);
            CancellationToken waitToken = linked?.Token ?? _closed.Token;
            bool readLockTaken = false;
            try
            {
                await _readLock.WaitAsync(waitToken).ConfigureAwait(false);
                readLockTaken = true;

                if (_eow)
                {
                    return ClosedReadResult;
                }

                bool canReadTaken = blockingMode == ReadBlockingMode.DoNotWait;
                if (canReadTaken && !_canRead.Wait(0))
                {
                    return _eow ? ClosedReadResult : ReadResult.Empty;
                }

                // Handle zero-length reads immediately to avoid deadlock with empty protobuf messages
                // WriteAsync returns early for zero-length writes without signaling _canRead
                // Only apply this optimization for WaitAll mode (exact length reads)
                // For WaitAny mode (ReadAllAsync), we need to wait for actual data
                if (length == 0 && blockingMode == ReadBlockingMode.WaitAll)
                {
                    return ReadResult.Ok(default);
                }

                if (!canReadTaken)
                {
                    await _canRead.WaitAsync(waitToken).ConfigureAwait(false);
                }

                if (_eow)
                {
                    if (!_bytes.IsEmpty)
                    {
                        Interlocked.And(ref _state, ~PendingWrite);
                        _bytes = default;
                        _read.Release();
                    }
                    return ClosedReadResult;
                }

                bool lockAgain = false;
                long bytesToRead = length != 0
                    ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Length))
                    : _bytes.Length;

                ReadOnlySequence<byte> chunk = default;
                MemorySegment<byte>? firstSegment = null;
                MemorySegment<byte>? lastSegment = null;
                do
                {
                    if (lockAgain) await _canRead.WaitAsync(waitToken).ConfigureAwait(false);

                    if (_eow)
                    {
                        if (!_bytes.IsEmpty)
                        {
                            Interlocked.And(ref _state, ~PendingWrite);
                            _bytes = default;
                            _read.Release();
                        }
                        return ClosedReadResult;
                    }

                    ReadOnlySequence<byte> anotherChunk = default;

                    if (_bytes.Length <= bytesToRead)
                    {
                        anotherChunk = _bytes;
                        bytesToRead -= _bytes.Length;
                        if (bytesToRead != 0)
                        {
                            if ((Interlocked.Or(ref _state, PendingRead) & Closed) != 0)
                            {
                                MarkAborted();
                            }
                        }
                        Interlocked.And(ref _state, ~PendingWrite);
                        _bytes = default;
                        _read.Release();
                    }
                    else if (_bytes.Length > bytesToRead)
                    {
                        anotherChunk = _bytes.Slice(0, bytesToRead);
                        _bytes = _bytes.Slice(bytesToRead, _bytes.End);
                        bytesToRead = 0;
                        _canRead.Release();
                    }

                    if (chunk.IsEmpty && firstSegment is null)
                    {
                        chunk = anotherChunk;
                    }
                    else
                    {
                        if (firstSegment is null)
                        {
                            foreach (ReadOnlyMemory<byte> segment in chunk)
                            {
                                if (firstSegment is null)
                                {
                                    firstSegment = lastSegment = new MemorySegment<byte>(segment);
                                }
                                else
                                {
                                    lastSegment = lastSegment!.Append(segment);
                                }
                            }
                        }

                        foreach (ReadOnlyMemory<byte> segment in anotherChunk)
                        {
                            lastSegment = lastSegment!.Append(segment);
                        }
                    }
                    lockAgain = true;
                } while (bytesToRead != 0);

                if (firstSegment is not null)
                {
                    chunk = new ReadOnlySequence<byte>(firstSegment, 0, lastSegment!, lastSegment!.Memory.Length);
                }

                Interlocked.And(ref _state, ~PendingRead);
                Libp2pMetrics.DataReceivedBytes.Add(chunk.Length);
                Libp2pMetrics.DataReceivedPackets.Add(1);
                return ReadResult.Ok(chunk);
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested
                    ? ClosedReadResult
                    : ReadResult.Cancelled;
            }
            finally
            {
                // Release the read lock on every path, including cancellation, so a cancelled
                // read can never leave it held and deadlock every subsequent read.
                if (readLockTaken)
                {
                    Interlocked.And(ref _state, ~PendingRead);
                    _readLock.Release();
                }
            }
        }

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            using CancellationTokenSource? linked = LinkToken(token);
            CancellationToken waitToken = linked?.Token ?? _closed.Token;
            bool canWriteTaken = false;
            try
            {
                await _canWrite.WaitAsync(waitToken).ConfigureAwait(false);
                canWriteTaken = true;

                if (_eow)
                {
                    return IOResult.Ended;
                }

                if (_bytes.Length != 0)
                {
                    return IOResult.InternalError;
                }

                if (bytes.Length == 0)
                {
                    return IOResult.Ok;
                }

                if ((Interlocked.Or(ref _state, PendingWrite) & Closed) != 0)
                {
                    Interlocked.And(ref _state, ~PendingWrite);
                    return IOResult.Ended;
                }
                _bytes = bytes;
                _canRead.Release();

                try
                {
                    await _read.WaitAsync(waitToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancelled after publishing the chunk. If no reader has taken the
                    // data-available signal yet, reclaim it and roll the publish back so the
                    // cancelled bytes are never delivered to a later read. Otherwise a reader
                    // is already committed to consuming, so wait without cancellation for it to
                    // finish and acknowledge on _read, keeping the channel consistent.
                    if (_canRead.Wait(0))
                    {
                        Interlocked.And(ref _state, ~PendingWrite);
                        _bytes = default;
                    }
                    else
                    {
                        await _read.WaitAsync().ConfigureAwait(false);
                    }

                    throw;
                }

                if (_closed.IsCancellationRequested)
                {
                    return IOResult.Ended;
                }

                Libp2pMetrics.DataSentBytes.Add(bytes.Length);
                Libp2pMetrics.DataSentPackets.Add(1);
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? IOResult.Ended : IOResult.Cancelled;
            }
            finally
            {
                // Keep ownership until this write has received its own acknowledgement.
                if (canWriteTaken)
                {
                    _canWrite.Release();
                }
            }
        }

        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            using CancellationTokenSource? linked = LinkToken(token);
            try
            {
                await _canWrite.WaitAsync(linked?.Token ?? _closed.Token).ConfigureAwait(false);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }
                _eow = true;
                _externalCompletionMonitor?.TryComplete();
                _canRead.Release();
                _canWrite.Release();
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? IOResult.Ended : IOResult.Cancelled;
            }
        }

        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
        {
            using CancellationTokenSource? linked = LinkToken(token);
            try
            {
                if (_eow)
                {
                    return ClosedIoResult;
                }
                await _readLock.WaitAsync(linked?.Token ?? _closed.Token).ConfigureAwait(false);
                _readLock.Release();
                return !_eow ? IOResult.Ok : ClosedIoResult;
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? ClosedIoResult : IOResult.Cancelled;
            }
        }

        private CancellationTokenSource? LinkToken(CancellationToken token)
            => token.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(token, _closed.Token) : null;
    }
}
