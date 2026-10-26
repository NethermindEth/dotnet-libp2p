// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]
[assembly: InternalsVisibleTo("Nethermind.Channels.Tests")]

namespace Nethermind.Channels;

public class Channel : IChannel
{
    private IChannel? _reversedChannel;
    private readonly ReaderWriter _reader;
    private readonly ReaderWriter _writer;
    private TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    public IChannel Reverse => _reversedChannel ??= new Channel(_writer, _reader)
    {
        _reversedChannel = this,
        Completion = Completion
    };

    public IReader Reader => _reader;
    public IWriter Writer => _writer;

    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        => _reader.ReadAsync(length, blockingMode, token);

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        => _writer.WriteAsync(bytes, token);

    public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        => _writer.WriteAsync(buffer, length, offset, token);

    public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        => _writer.WriteAsync(slice, token);

    public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
        => _writer.WriteAsync(slices, token);

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _writer.WriteEofAsync(token);
    public TaskAwaiter GetAwaiter() => Completion.Task.GetAwaiter();

    public ValueTask CloseAsync()
    {
        bool inboundIncomplete = _reader.MarkClosed();
        bool outboundIncomplete = _writer.MarkClosed();
        if (inboundIncomplete || outboundIncomplete)
            _reader.MarkAborted();
        // Discarded inbound data must not abort an already completed outbound reply.
        if (outboundIncomplete || (inboundIncomplete && !_writer._eow))
            _writer.MarkAborted();
        _reader.CompleteAbort();
        _writer.CompleteAbort();
        Completion.TrySetResult();
        return ValueTask.CompletedTask;
    }

    internal ValueTask CompleteProtocolAsync()
    {
        _writer.TryWriteEof();
        return CloseAsync();
    }

    public ValueTask AbortAsync()
    {
        _reader.MarkAborted();
        _writer.MarkAborted();
        return CloseAsync();
    }

    public ValueTask CloseReadAsync()
    {
        _reader.MarkClosed();
        _reader.MarkAborted();
        _reader.CompleteAbort();
        return ValueTask.CompletedTask;
    }

    private void TryComplete()
    {
        if (_reader._eow && _writer._eow) Completion.TrySetResult();
    }

    internal class ReaderWriter : IReader, IWriter
    {
        private const int Closed = 1;
        private const int PendingWrite = 2;
        private const int PendingRead = 4;
        private const int WriteDiscarded = 8;

        private readonly Lock _dataLock = new();
        private ReadOnlySequence<byte> _bytes;
        private PooledBuffer.Slice? _owner;
        private int _ownerOffset;
        private readonly AsyncSemaphore _canWrite = new(true);
        private readonly AsyncSemaphore _read = new(false);
        private readonly AsyncSemaphore _canRead = new(false);
        private readonly AsyncSemaphore _readLock = new(true);
        private readonly CancellationTokenSource _closed = new();
        private readonly Channel? _externalCompletionMonitor;
        internal volatile bool _eow;
        private int _aborted;
        private int _state;

        internal ReaderWriter(Channel completionMonitor) => _externalCompletionMonitor = completionMonitor;
        public ReaderWriter() { }
        internal bool IsWriteEnded => _eow;
        private ReadResult ClosedReadResult => Volatile.Read(ref _aborted) != 0 ? ReadResult.Aborted : ReadResult.Ended;
        private IOResult ClosedIoResult => Volatile.Read(ref _aborted) != 0 ? IOResult.Aborted : IOResult.Ended;

        internal bool MarkClosed()
        {
            lock (_dataLock)
            {
                int previous = Interlocked.Or(ref _state, Closed);
                return (previous & (PendingWrite | PendingRead)) != 0;
            }
        }

        internal void MarkAborted() => Interlocked.Exchange(ref _aborted, 1);

        internal bool TryWriteEof()
        {
            lock (_dataLock)
            {
                if (_eow) return true;
                if (!_canWrite.TryWait()) return false;
                try
                {
                    _eow = true;
                    _externalCompletionMonitor?.TryComplete();
                    _canRead.Release();
                    return true;
                }
                finally
                {
                    // Publish EOF before another completion can observe this reservation.
                    _canWrite.Release();
                }
            }
        }

        internal void CompleteAbort()
        {
            lock (_dataLock)
            {
                _eow = true;
                if (!_bytes.IsEmpty)
                {
                    Interlocked.And(ref _state, ~PendingWrite);
                    Interlocked.Or(ref _state, WriteDiscarded);
                    DiscardPublished();
                    _read.Release();
                }
            }
            _closed.Cancel();
            _externalCompletionMonitor?.TryComplete();
        }

        private void DiscardPublished()
        {
            _bytes = default;
            _owner?.Dispose();
            _owner = null;
            _ownerOffset = 0;
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            bool readLockTaken = false;
            ReadBuffer accumulated = default;
            try
            {
                await _readLock.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                readLockTaken = true;
                if (_eow) return ClosedReadResult;
                if (length == 0 && blockingMode == ReadBlockingMode.WaitAll) return ReadResult.Empty;

                if (blockingMode == ReadBlockingMode.DoNotWait)
                {
                    if (!_canRead.TryWait()) return _eow ? ClosedReadResult : ReadResult.Empty;
                }
                else
                {
                    await _canRead.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                }

                long remaining;
                lock (_dataLock)
                {
                    if (_eow) return ClosedReadResult;
                    remaining = length == 0 ? _bytes.Length
                        : blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Length);
                }

                do
                {
                    ReadOnlySequence<byte> part;
                    PooledBuffer.Slice? owner = null;
                    bool consumed;
                    lock (_dataLock)
                    {
                        if (_eow) return ClosedReadResult;
                        long take = Math.Min(remaining, _bytes.Length);
                        part = _bytes.Slice(0, take);
                        consumed = take == _bytes.Length;
                        if (_owner is not null)
                        {
                            if (consumed && _ownerOffset == 0)
                            {
                                owner = _owner;
                            }
                            else
                            {
                                owner = _owner.LeaseSlice(_ownerOffset, checked((int)take));
                                if (consumed) _owner.Dispose();
                            }

                            if (consumed)
                            {
                                _owner = null;
                                _ownerOffset = 0;
                            }
                            else
                            {
                                _ownerOffset += checked((int)take);
                            }
                        }
                        _bytes = _bytes.Slice(take);
                        remaining -= take;
                        if (consumed && remaining != 0 && (Interlocked.Or(ref _state, PendingRead) & Closed) != 0)
                            MarkAborted();
                    }

                    try
                    {
                        // Ownership was transferred under the lock; copying may now happen outside it.
                        // Release consumed owners before acknowledging, allowing receive storage to be reused.
                        accumulated.Append(part, owner);
                    }
                    catch
                    {
                        MarkAborted();
                        if (consumed)
                        {
                            lock (_dataLock)
                            {
                                Interlocked.And(ref _state, ~PendingWrite);
                                Interlocked.Or(ref _state, WriteDiscarded);
                                _read.Release();
                            }
                        }
                        CompleteAbort();
                        throw;
                    }

                    lock (_dataLock)
                    {
                        if (consumed)
                        {
                            Interlocked.And(ref _state, ~PendingWrite);
                            if (_eow) Interlocked.Or(ref _state, WriteDiscarded);
                            if (remaining == 0) Interlocked.And(ref _state, ~PendingRead);
                            _read.Release();
                        }
                        else if (!_eow)
                        {
                            _canRead.Release();
                        }
                        if (_eow) return ClosedReadResult;
                    }

                    if (remaining != 0) await _canRead.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                } while (remaining != 0);

                return accumulated.MoveToResult();
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? ClosedReadResult : ReadResult.Cancelled;
            }
            finally
            {
                accumulated.Dispose();
                if (readLockTaken)
                {
                    Interlocked.And(ref _state, ~PendingRead);
                    _readLock.Release();
                }
            }
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => WriteCoreAsync(bytes, null, token);

        public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            PooledBuffer.Slice owner = buffer.LeaseSlice(offset, length);
            return WriteCoreAsync(new ReadOnlySequence<byte>(owner.ReadOnlyMemory), owner, token);
        }

        public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(slice);
            PooledBuffer.Slice owner = slice.LeaseSlice(0, slice.Length);
            return WriteCoreAsync(new ReadOnlySequence<byte>(owner.ReadOnlyMemory), owner, token);
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
        {
            if (slices.Length == 0) return WriteAsync(ReadOnlySequence<byte>.Empty, token);
            if (slices.Length == 1) return WriteAsync(slices[0], token);
            int length = 0;
            foreach (PooledBuffer.Slice slice in slices)
            {
                ArgumentNullException.ThrowIfNull(slice);
                length = checked(length + slice.Length);
            }

            using PooledBuffer combined = PooledBuffer.Rent(length);
            int offset = 0;
            foreach (PooledBuffer.Slice slice in slices)
            {
                slice.ReadOnlySpan.CopyTo(combined.Span[offset..]);
                offset += slice.Length;
            }
            return WriteAsync(combined, length, token: token);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<IOResult> WriteCoreAsync(ReadOnlySequence<byte> bytes,
            PooledBuffer.Slice? owner, CancellationToken token)
        {
            bool canWriteTaken = false;
            try
            {
                await _canWrite.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                canWriteTaken = true;
                lock (_dataLock)
                {
                    if (_eow) return ClosedIoResult;
                    if (!_bytes.IsEmpty) return IOResult.InternalError;
                    if (bytes.IsEmpty) return IOResult.Ok;
                    if ((Interlocked.Or(ref _state, PendingWrite) & Closed) != 0)
                    {
                        Interlocked.And(ref _state, ~PendingWrite);
                        return ClosedIoResult;
                    }
                    _bytes = bytes;
                    _owner = owner;
                    owner = null;
                    _ownerOffset = 0;
                    _canRead.Release();
                }

                try
                {
                    await _read.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    if (_canRead.TryWait())
                    {
                        lock (_dataLock)
                        {
                            if (_bytes.Length == bytes.Length)
                            {
                                Interlocked.And(ref _state, ~PendingWrite);
                                DiscardPublished();
                                throw;
                            }
                        }
                        // A partial read committed the stream to delivering the remainder.
                        _canRead.Release();
                    }
                    await _read.WaitAsync(default, _closed.Token).ConfigureAwait(false);
                }

                return (Interlocked.And(ref _state, ~WriteDiscarded) & WriteDiscarded) != 0
                    ? ClosedIoResult : IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? ClosedIoResult : IOResult.Cancelled;
            }
            finally
            {
                owner?.Dispose();
                // Keep the write gate until this write receives its own acknowledgement.
                if (canWriteTaken) _canWrite.Release();
            }
        }

        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            bool canWriteTaken = false;
            try
            {
                await _canWrite.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                canWriteTaken = true;
                lock (_dataLock)
                {
                    if (_eow) return ClosedIoResult;
                    _eow = true;
                    _externalCompletionMonitor?.TryComplete();
                    _canRead.Release();
                    return IOResult.Ok;
                }
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? ClosedIoResult : IOResult.Cancelled;
            }
            finally
            {
                if (canWriteTaken) _canWrite.Release();
            }
        }

        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
        {
            try
            {
                if (_eow) return ClosedIoResult;
                await _readLock.WaitAsync(token, _closed.Token).ConfigureAwait(false);
                _readLock.Release();
                return _eow ? ClosedIoResult : IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return _closed.IsCancellationRequested ? ClosedIoResult : IOResult.Cancelled;
            }
        }
    }

    private struct ReadBuffer : IDisposable
    {
        private ReadOnlySequence<byte> _sequence;
        private PooledBuffer.Slice? _owner;
        private PooledBuffer? _combined;
        private int _written;
        private MemorySegment? _first;
        private MemorySegment? _last;

        public void Append(ReadOnlySequence<byte> bytes, PooledBuffer.Slice? owner)
        {
            try
            {
                if (_sequence.IsEmpty && _combined is null)
                {
                    _sequence = bytes;
                    _owner = owner;
                    owner = null;
                    return;
                }

                if (_owner is not null || owner is not null || _combined is not null)
                {
                    int previousLength = _combined is null ? checked((int)_sequence.Length) : _written;
                    int required = checked(previousLength + (int)bytes.Length);
                    if (_combined is null || _combined.Length < required)
                    {
                        int capacity = (int)Math.Max(required, Math.Min(int.MaxValue, Math.Max(16L, previousLength * 2L)));
                        PooledBuffer expanded = PooledBuffer.Rent(capacity);
                        try
                        {
                            if (_combined is null) _sequence.CopyTo(expanded.Span);
                            else _combined.ReadOnlySpan[.._written].CopyTo(expanded.Span);
                        }
                        catch
                        {
                            expanded.Dispose();
                            throw;
                        }
                        _combined?.Dispose();
                        _combined = expanded;
                    }
                    bytes.CopyTo(_combined.Span[previousLength..]);
                    _written = required;
                    _owner?.Dispose();
                    _owner = null;
                    _sequence = default;
                    _first = _last = null;
                    return;
                }

                if (_first is null)
                {
                    foreach (ReadOnlyMemory<byte> segment in _sequence) AppendSegment(segment);
                }
                foreach (ReadOnlyMemory<byte> segment in bytes) AppendSegment(segment);
                _sequence = new ReadOnlySequence<byte>(_first!, 0, _last!, _last!.Memory.Length);
            }
            finally
            {
                owner?.Dispose();
            }
        }

        private void AppendSegment(ReadOnlyMemory<byte> memory)
        {
            if (memory.IsEmpty) return;
            MemorySegment next = new(memory);
            if (_last is null) _first = next;
            else _last.Append(next);
            _last = next;
        }

        public ReadResult MoveToResult()
        {
            if (_combined is not null)
            {
                PooledBuffer.Slice owner = _combined.LeaseSlice(0, _written);
                _combined.Dispose();
                _combined = null;
                return ReadResult.Ok(owner);
            }
            if (_owner is not null)
            {
                PooledBuffer.Slice owner = _owner;
                _owner = null;
                return ReadResult.Ok(owner);
            }
            return ReadResult.Ok(_sequence);
        }

        public void Dispose()
        {
            _owner?.Dispose();
            _combined?.Dispose();
        }
    }

    private sealed class MemorySegment : ReadOnlySequenceSegment<byte>
    {
        public MemorySegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public void Append(MemorySegment next)
        {
            next.RunningIndex = RunningIndex + Memory.Length;
            Next = next;
        }
    }
}
