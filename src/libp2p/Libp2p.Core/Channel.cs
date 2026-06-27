// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Nethermind.Libp2p.Core.Metrics;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]
[assembly: InternalsVisibleTo("Libp2p.Protocols.Pubsub.Tests")]

namespace Nethermind.Libp2p.Core;

public class Channel : IChannel
{
    private IChannel? _reversedChannel;
    private readonly ReaderWriter _reader;
    private readonly ReaderWriter _writer;
    private readonly ChannelBufferHints _bufferHints;
    private TaskCompletionSource Completion = new();

    public Channel() : this(default)
    {
    }

    private Channel(ChannelBufferHints bufferHints)
    {
        _reader = new ReaderWriter(this);
        _writer = new ReaderWriter(this);
        _bufferHints = bufferHints;
    }

    private Channel(ReaderWriter reader, ReaderWriter writer, ChannelBufferHints bufferHints)
    {
        _reader = reader;
        _writer = writer;
        _bufferHints = bufferHints;
    }

    internal static (IChannel Channel, IChannel Reverse) CreatePair(
        ChannelBufferHints channelHints = default,
        ChannelBufferHints reverseHints = default) => CreateDefaultPair(channelHints, reverseHints);

    internal static (IChannel Channel, IChannel Reverse) CreateDefaultPair(ChannelBufferHints channelHints, ChannelBufferHints reverseHints)
    {
        Channel channel = new(channelHints);
        Channel reverse = new((ReaderWriter)channel.Writer, (ReaderWriter)channel.Reader, reverseHints)
        {
            _reversedChannel = channel,
            Completion = channel.Completion
        };
        channel._reversedChannel = reverse;
        return (channel, reverse);
    }

    public IChannel Reverse
    {
        get => _reversedChannel ??= new Channel((ReaderWriter)Writer, (ReaderWriter)Reader, default)
        {
            _reversedChannel = this,
            Completion = Completion
        };
    }

    public ChannelBufferHints BufferHints => _bufferHints;

    public IReader Reader => _reader;
    public IWriter Writer => _writer;

    public ValueTask<ReadResult> ReadAsync(int length,
        ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        return Reader.ReadAsync(length, blockingMode, token);
    }

    public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
    {
        return Writer.WriteAsync(buffer, length, offset, token);
    }

    public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
    {
        return Writer.WriteAsync(slice, token);
    }

    public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, int length, int offset = 0, CancellationToken token = default)
    {
        return Writer.WriteAsync(slice, length, offset, token);
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
    {
        return Writer.WriteAsync(slices, token);
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        return Writer.WriteAsync(bytes, token);
    }

    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Writer.WriteEofAsync(token);

    public TaskAwaiter GetAwaiter() => Completion.Task.GetAwaiter();

    public async ValueTask CloseAsync()
    {
        ValueTask<IOResult> stopReader = _reader.WriteEofAsync().Preserve();
        await _writer.WriteEofAsync();
        if (!stopReader.IsCompleted)
        {
            await stopReader;
        }
        Completion.TrySetResult();
    }

    private void TryComplete()
    {
        if (_reader._eow && _writer._eow)
        {
            Completion.TrySetResult();
        }
    }

    internal sealed class ReaderWriter : IReader, IWriter
    {
        private PooledBuffer? _buffer;
        private int _offset;
        private int _prependLimit;
        private int _remaining;
        private PooledBuffer.Slice[]? _slices;
        private int _sliceCount;
        private int _segmentIndex;
        private int _segmentOffset;
        private int _totalRemaining;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly AsyncSignal _canRead = new();
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private readonly WriteCompletion _writeCompletion = new();
        private WriteCompletion? _currentWriteCompletion;
        private int _currentWriteLength;
        private readonly Channel? _externalCompletionMonitor;
        internal bool _eow;

        internal ReaderWriter(Channel tryComplete)
        {
            _externalCompletionMonitor = tryComplete;
        }

        public ReaderWriter()
        {
        }

        public ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            return ReadAsyncSlow(length, blockingMode, token);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<ReadResult> ReadAsyncSlow(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);

            PooledBuffer? rented = null;

            try
            {
                await _readLock.WaitAsync(token);

                try
                {
                    if (_eow && !HasData)
                    {
                        return ReadResult.Ended;
                    }

                    if (blockingMode == ReadBlockingMode.DoNotWait && !HasData)
                    {
                        return ReadResult.Empty;
                    }

                    if (length == 0 && blockingMode == ReadBlockingMode.WaitAll)
                    {
                        return ReadResult.Empty;
                    }

                    await _canRead.WaitAsync(token);

                    if (_eow && !HasData)
                    {
                        return ReadResult.Ended;
                    }

                    if (!HasData)
                    {
                        return ReadResult.Empty;
                    }

                    int target = GetReadTarget(length, blockingMode);
                    if (target == 0)
                    {
                        if (HasData)
                        {
                            _canRead.Release();
                        }
                        return ReadResult.Empty;
                    }

                    if (CanReadZeroCopy(target))
                    {
                        return ReadZeroCopyAndTrack(target);
                    }

                    rented = PooledBuffer.Rent(target);
                    int written = 0;

                    while (written < target)
                    {
                        if (!HasData)
                        {
                            if (_eow)
                            {
                                rented.Dispose();
                                rented = null;
                                return ReadResult.Ended;
                            }

                            await _canRead.WaitAsync(token);

                            if (_eow && !HasData)
                            {
                                rented.Dispose();
                                rented = null;
                                return ReadResult.Ended;
                            }
                        }

                        int toTake = blockingMode == ReadBlockingMode.WaitAll
                            ? Math.Min(target - written, Available)
                            : target - written;

                        bool hasRemainingInCurrentWrite = CopyTo(rented.Span.Slice(written, toTake), toTake);
                        written += toTake;

                        if (blockingMode != ReadBlockingMode.WaitAll)
                        {
                            SignalReadableIfNeeded(hasRemainingInCurrentWrite);
                            break;
                        }

                        if (written == target)
                        {
                            SignalReadableIfNeeded(hasRemainingInCurrentWrite);
                        }
                    }

                    PooledBuffer resultBuffer = rented;
                    ReadResult readResult = ReadResult.Ok(resultBuffer, 0, written);
                    rented = null;

                    TrackRead(readResult.Length);
                    return readResult;
                }
                finally
                {
                    _readLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                rented?.Dispose();
                return ReadResult.Cancelled;
            }
        }

        public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        {
            try
            {
                if ((uint)offset > (uint)buffer.Length || (uint)length > (uint)(buffer.Length - offset))
                {
                    return new ValueTask<IOResult>(IOResult.InternalError);
                }

                if (!_canWrite.Wait(0, token))
                {
                    return WriteAsyncSlow(buffer, length, offset, token);
                }

                if (_eow)
                {
                    _canWrite.Release();
                    return new ValueTask<IOResult>(IOResult.Ended);
                }

                if (HasData)
                {
                    _canWrite.Release();
                    return new ValueTask<IOResult>(IOResult.InternalError);
                }

                if (length == 0)
                {
                    _canWrite.Release();
                    return new ValueTask<IOResult>(IOResult.Ok);
                }

                buffer.Retain();
                _buffer = buffer;
                _offset = offset;
                _prependLimit = offset;
                _remaining = length;
                return WaitForCurrentWriteReadAsync(length, token);
            }
            catch (OperationCanceledException)
            {
                return new ValueTask<IOResult>(IOResult.Cancelled);
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<IOResult> WriteAsyncSlow(PooledBuffer buffer, int length, int offset, CancellationToken token)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                if (HasData)
                {
                    _canWrite.Release();
                    return IOResult.InternalError;
                }

                if (length == 0)
                {
                    _canWrite.Release();
                    return IOResult.Ok;
                }

                buffer.Retain();
                _buffer = buffer;
                _offset = offset;
                _prependLimit = offset;
                _remaining = length;
                return await WaitForCurrentWriteReadAsync(length, token);
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        private ValueTask<IOResult> WaitForCurrentWriteReadAsync(int length, CancellationToken token)
        {
            _currentWriteLength = length;

            if (!token.CanBeCanceled && _writeCompletion.TryStart(out ValueTask<IOResult> completion))
            {
                _currentWriteCompletion = _writeCompletion;
                SignalCanRead();
                return completion;
            }

            _currentWriteCompletion = null;
            SignalCanRead();
            return WaitForCurrentWriteReadSlowAsync(token);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<IOResult> WaitForCurrentWriteReadSlowAsync(CancellationToken token)
        {
            try
            {
                await _read.WaitAsync(token);
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        {
            return WriteAsync(slice, slice.Length, 0, token);
        }

        public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, int length, int offset = 0, CancellationToken token = default)
        {
            if ((uint)offset > (uint)slice.Length || (uint)length > (uint)(slice.Length - offset))
            {
                return new ValueTask<IOResult>(IOResult.InternalError);
            }

            return length == 0
                ? new ValueTask<IOResult>(IOResult.Ok)
                : WriteSliceAsync(slice, length, offset, token);
        }

        private ValueTask<IOResult> WriteSliceAsync(PooledBuffer.Slice slice, int length, int offset, CancellationToken token)
        {
            int writeOffset = slice.Offset + offset;
            int prependLimit = offset == 0 ? slice.PrependLimit : writeOffset;

            try
            {
                if (!_canWrite.Wait(0, token))
                {
                    return WriteSliceAsyncSlow(slice, length, writeOffset, prependLimit, token);
                }

                if (_eow)
                {
                    _canWrite.Release();
                    return new ValueTask<IOResult>(IOResult.Ended);
                }

                if (HasData)
                {
                    _canWrite.Release();
                    return new ValueTask<IOResult>(IOResult.InternalError);
                }

                PooledBuffer buffer = slice.Owner;
                buffer.Retain();
                _buffer = buffer;
                _offset = writeOffset;
                _prependLimit = prependLimit;
                _remaining = length;
                return WaitForCurrentWriteReadAsync(length, token);
            }
            catch (OperationCanceledException)
            {
                return new ValueTask<IOResult>(IOResult.Cancelled);
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<IOResult> WriteSliceAsyncSlow(
            PooledBuffer.Slice slice,
            int length,
            int offset,
            int prependLimit,
            CancellationToken token)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                if (HasData)
                {
                    _canWrite.Release();
                    return IOResult.InternalError;
                }

                PooledBuffer buffer = slice.Owner;
                buffer.Retain();
                _buffer = buffer;
                _offset = offset;
                _prependLimit = prependLimit;
                _remaining = length;
                return await WaitForCurrentWriteReadAsync(length, token);
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
        {
            if (slices.Length == 0)
            {
                return new ValueTask<IOResult>(IOResult.Ok);
            }

            PooledBuffer.Slice[] retained = ArrayPool<PooledBuffer.Slice>.Shared.Rent(slices.Length);
            int count = 0;
            int length = 0;

            try
            {
                for (int i = 0; i < slices.Length; i++)
                {
                    PooledBuffer.Slice slice = slices[i];
                    if (slice.Length == 0)
                    {
                        continue;
                    }

                    retained[count++] = slice.Retain();
                    length += slice.Length;
                }
            }
            catch
            {
                DisposeSlices(retained, count);
                ArrayPool<PooledBuffer.Slice>.Shared.Return(retained, clearArray: true);
                throw;
            }

            if (count == 0)
            {
                ArrayPool<PooledBuffer.Slice>.Shared.Return(retained, clearArray: true);
                return new ValueTask<IOResult>(IOResult.Ok);
            }

            return WriteSlicesAsync(retained, count, length, token);
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private async ValueTask<IOResult> WriteSlicesAsync(PooledBuffer.Slice[] slices, int count, int length, CancellationToken token)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    DisposeSlices(slices, count);
                    ArrayPool<PooledBuffer.Slice>.Shared.Return(slices, clearArray: true);
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                if (HasData)
                {
                    DisposeSlices(slices, count);
                    ArrayPool<PooledBuffer.Slice>.Shared.Return(slices, clearArray: true);
                    _canWrite.Release();
                    return IOResult.InternalError;
                }

                _slices = slices;
                _sliceCount = count;
                _segmentIndex = 0;
                _segmentOffset = 0;
                _totalRemaining = length;

                SignalCanRead();
                await _read.WaitAsync(token);
                TrackWrite(length);
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                _eow = true;
                _externalCompletionMonitor?.TryComplete();
                SignalCanRead();
                _canWrite.Release();
                return IOResult.Ok;
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
        {
            try
            {
                if (_eow && !HasData)
                {
                    return IOResult.Ended;
                }

                await _readLock.WaitAsync(token);
                _readLock.Release();
                return !_eow || HasData ? IOResult.Ok : IOResult.Ended;
            }
            catch (OperationCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        private bool HasData => Available > 0;

        private int Available => _buffer is not null ? _remaining : _totalRemaining;

        private int GetReadTarget(int length, ReadBlockingMode blockingMode)
        {
            if (length == 0)
            {
                return Available;
            }

            return blockingMode == ReadBlockingMode.WaitAll
                ? length
                : Math.Min(length, Available);
        }

        private bool CanReadZeroCopy(int length)
        {
            if (_buffer is not null)
            {
                return length <= _remaining;
            }

            if (_slices is null || _segmentIndex >= _sliceCount)
            {
                return false;
            }

            ref PooledBuffer.Slice slice = ref _slices[_segmentIndex];
            return length <= slice.Length - _segmentOffset;
        }

        private ReadResult ReadZeroCopyAndTrack(int length)
        {
            ReadResult result = ReadZeroCopy(length, out bool hasRemainingInCurrentWrite);
            SignalReadableIfNeeded(hasRemainingInCurrentWrite);
            TrackRead(result.Length);
            return result;
        }

        private ReadResult ReadZeroCopy(int length, out bool hasRemainingInCurrentWrite)
        {
            if (_buffer is not null)
            {
                PooledBuffer buffer = _buffer;
                int offset = _offset;
                int prependLimit = _prependLimit;
                buffer.Retain();
                hasRemainingInCurrentWrite = Advance(length);
                return ReadResult.Ok(buffer, offset, length, prependLimit);
            }

            ref PooledBuffer.Slice slice = ref _slices![_segmentIndex];
            PooledBuffer owner = slice.Owner;
            int start = slice.Offset + _segmentOffset;
            int slicePrependLimit = _segmentOffset == 0 ? slice.PrependLimit : start;
            owner.Retain();
            hasRemainingInCurrentWrite = Advance(length);
            return ReadResult.Ok(owner, start, length, slicePrependLimit);
        }

        private bool CopyTo(Span<byte> destination, int length)
        {
            int copied = 0;
            bool hasRemainingInCurrentWrite = false;
            while (copied < length)
            {
                if (_buffer is not null)
                {
                    int take = Math.Min(length - copied, _remaining);
                    new ReadOnlySpan<byte>(_buffer.Array, _offset, take).CopyTo(destination[copied..]);
                    hasRemainingInCurrentWrite = Advance(take);
                    copied += take;
                    continue;
                }

                ref PooledBuffer.Slice slice = ref _slices![_segmentIndex];
                int available = slice.Length - _segmentOffset;
                int sliceTake = Math.Min(length - copied, available);
                new ReadOnlySpan<byte>(slice.Owner.Array, slice.Offset + _segmentOffset, sliceTake).CopyTo(destination[copied..]);
                hasRemainingInCurrentWrite = Advance(sliceTake);
                copied += sliceTake;
            }

            return hasRemainingInCurrentWrite;
        }

        private bool Advance(int length)
        {
            if (_buffer is not null)
            {
                _offset += length;
                _remaining -= length;

                if (_remaining == 0)
                {
                    _buffer.Release();
                    _buffer = null;
                    _offset = 0;
                    _prependLimit = 0;
                    CompleteCurrentWrite();
                    return false;
                }

                _prependLimit = _offset;
                return true;
            }

            while (length > 0 && _slices is not null)
            {
                ref PooledBuffer.Slice slice = ref _slices[_segmentIndex];
                int available = slice.Length - _segmentOffset;
                int take = Math.Min(length, available);
                _segmentOffset += take;
                _totalRemaining -= take;
                length -= take;

                if (_segmentOffset == slice.Length)
                {
                    slice.Dispose();
                    _segmentIndex++;
                    _segmentOffset = 0;
                    if (_segmentIndex == _sliceCount)
                    {
                        ArrayPool<PooledBuffer.Slice>.Shared.Return(_slices, clearArray: true);
                        _slices = null;
                        _sliceCount = 0;
                        CompleteCurrentWrite();
                        return false;
                    }
                }
            }

            return _slices is not null && _totalRemaining > 0;
        }

        private void SignalCanRead()
        {
            _canRead.Release();
        }

        private void CompleteCurrentWrite()
        {
            WriteCompletion? completion = _currentWriteCompletion;
            _currentWriteCompletion = null;
            int length = _currentWriteLength;
            _currentWriteLength = 0;
            TrackWrite(length);
            _canWrite.Release();

            if (completion is not null)
            {
                completion.SetResult(IOResult.Ok);
                return;
            }

            _read.Release();
        }

        private void SignalReadableIfNeeded(bool hasRemainingInCurrentWrite)
        {
            if (hasRemainingInCurrentWrite)
            {
                SignalCanRead();
            }
        }

        private static void DisposeSlices(PooledBuffer.Slice[] slices, int count)
        {
            for (int i = 0; i < count; i++)
            {
                slices[i].Dispose();
            }
        }

        private static void TrackRead(int length)
        {
            if (Libp2pMetrics.DataReceivedBytes.Enabled)
            {
                Libp2pMetrics.DataReceivedBytes.Add(length);
            }

            if (Libp2pMetrics.DataReceivedPackets.Enabled)
            {
                Libp2pMetrics.DataReceivedPackets.Add(1);
            }
        }

        private static void TrackWrite(int length)
        {
            if (Libp2pMetrics.DataSentBytes.Enabled)
            {
                Libp2pMetrics.DataSentBytes.Add(length);
            }

            if (Libp2pMetrics.DataSentPackets.Enabled)
            {
                Libp2pMetrics.DataSentPackets.Add(1);
            }
        }

        private sealed class WriteCompletion : IValueTaskSource<IOResult>
        {
            private ManualResetValueTaskSourceCore<IOResult> _source = new() { RunContinuationsAsynchronously = true };
            private int _inUse;

            public bool TryStart(out ValueTask<IOResult> completion)
            {
                if (Interlocked.CompareExchange(ref _inUse, 1, 0) != 0)
                {
                    completion = default;
                    return false;
                }

                _source.Reset();
                completion = new ValueTask<IOResult>(this, _source.Version);
                return true;
            }

            public void SetResult(IOResult result)
            {
                _source.SetResult(result);
            }

            public IOResult GetResult(short token)
            {
                try
                {
                    return _source.GetResult(token);
                }
                finally
                {
                    Volatile.Write(ref _inUse, 0);
                }
            }

            public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

            public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                _source.OnCompleted(continuation, state, token, flags);
            }
        }

        private sealed class AsyncSignal : IValueTaskSource<bool>
        {
            private readonly object _sync = new();
            private ManualResetValueTaskSourceCore<bool> _source = new() { RunContinuationsAsynchronously = true };
            private bool _signaled;
            private bool _waiting;
            private TaskCompletionSource<bool>? _cancellableWaiter;
            private CancellationTokenRegistration _cancellationRegistration;

            public ValueTask<bool> WaitAsync(CancellationToken token)
            {
                if (token.CanBeCanceled)
                {
                    return WaitWithCancellationAsync(token);
                }

                lock (_sync)
                {
                    if (_signaled)
                    {
                        _signaled = false;
                        return new ValueTask<bool>(true);
                    }

                    if (_waiting)
                    {
                        throw new InvalidOperationException("Only one channel reader can wait for data at a time.");
                    }

                    _source.Reset();
                    _waiting = true;
                    return new ValueTask<bool>(this, _source.Version);
                }
            }

            public void Release()
            {
                TaskCompletionSource<bool>? cancellableWaiter;
                CancellationTokenRegistration cancellationRegistration;
                bool completePooledWaiter;

                lock (_sync)
                {
                    if (!_waiting)
                    {
                        if (_signaled)
                        {
                            throw new SemaphoreFullException();
                        }

                        _signaled = true;
                        return;
                    }

                    _waiting = false;
                    cancellableWaiter = _cancellableWaiter;
                    _cancellableWaiter = null;
                    cancellationRegistration = _cancellationRegistration;
                    _cancellationRegistration = default;
                    completePooledWaiter = cancellableWaiter is null;
                }

                cancellationRegistration.Dispose();

                if (completePooledWaiter)
                {
                    _source.SetResult(true);
                    return;
                }

                cancellableWaiter!.SetResult(true);
            }

            public bool GetResult(short token) => _source.GetResult(token);
            public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

            public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            {
                _source.OnCompleted(continuation, state, token, flags);
            }

            private ValueTask<bool> WaitWithCancellationAsync(CancellationToken token)
            {
                if (token.IsCancellationRequested)
                {
                    return ValueTask.FromCanceled<bool>(token);
                }

                lock (_sync)
                {
                    if (_signaled)
                    {
                        _signaled = false;
                        return new ValueTask<bool>(true);
                    }

                    if (_waiting)
                    {
                        throw new InvalidOperationException("Only one channel reader can wait for data at a time.");
                    }

                    TaskCompletionSource<bool> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _cancellableWaiter = waiter;
                    _waiting = true;
                    _cancellationRegistration = token.Register(static state => ((AsyncSignal)state!).Cancel(), this);
                    return new ValueTask<bool>(waiter.Task);
                }
            }

            private void Cancel()
            {
                TaskCompletionSource<bool>? waiter;

                lock (_sync)
                {
                    waiter = _cancellableWaiter;
                    if (!_waiting || waiter is null)
                    {
                        return;
                    }

                    _waiting = false;
                    _cancellableWaiter = null;
                    _cancellationRegistration = default;
                }

                waiter.SetCanceled();
            }
        }

    }
}
