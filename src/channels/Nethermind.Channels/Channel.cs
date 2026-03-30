// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]
[assembly: InternalsVisibleTo("Libp2p.Protocols.Pubsub.Tests")]

namespace Nethermind.Channels;

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


    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        return Reader.ReadAsync(length, blockingMode, token);
    }

    public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
    {
        return Writer.WriteAsync(buffer, length, offset, token);
    }

    public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
    {
        return Writer.WriteAsync(slices, token);
    }

    public ValueTask<IOResult> WriteAsync(params PooledBuffer.Slice[] slices)
    {
        return Writer.WriteAsync((ReadOnlySpan<PooledBuffer.Slice>)slices, default);
    }

    public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
    {
        return Writer.WriteAsync(slice, token);
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


    internal class ReaderWriter : IReader, IWriter
    {
        internal protected ReaderWriter(Channel tryComplete)
        {
            _externalCompletionMonitor = tryComplete;
        }

        public ReaderWriter()
        {
        }

        private PooledBuffer? _buffer;
        private int _offset;
        private int _remaining;
        private List<PooledBuffer.Slice>? _slices;
        private int _segmentIndex;
        private int _segmentOffset;
        private int _totalRemaining;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly SemaphoreSlim _canRead = new(0, 1);
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private readonly Channel? _externalCompletionMonitor;
        internal bool _eow = false;

        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            try
            {
                await _readLock.WaitAsync(token);

                if (_eow && _buffer is null && (_slices is null || _totalRemaining == 0))
                {
                    _readLock.Release();
                    return ReadResult.Ended;
                }

                if (blockingMode == ReadBlockingMode.DontWait && (_buffer is null && (_slices is null || _totalRemaining == 0)))
                {
                    _readLock.Release();
                    return ReadResult.Empty;
                }

                await _canRead.WaitAsync(token);

                if (_eow && _buffer is null && (_slices is null || _totalRemaining == 0))
                {
                    _canRead.Release();
                    _readLock.Release();
                    _read.Release();
                    return ReadResult.Ended;
                }

                if (_buffer is null && (_slices is null || _totalRemaining == 0))
                {
                    _readLock.Release();
                    return ReadResult.Empty;
                }

                ReadOnlyMemory<byte> chunk;
                PooledBuffer? ownedBuffer = null;
                PooledBuffer? resultBuffer = null;
                int resultOffset = 0;
                int resultLength = 0;

                if (_slices is null)
                {
                    int bytesToRead = _remaining;
                    if (length != 0)
                    {
                        bytesToRead = Math.Min(length, _remaining);
                    }

                    chunk = new ReadOnlyMemory<byte>(_buffer!.Array, _offset, bytesToRead);
                    resultBuffer = _buffer;
                    resultOffset = _offset;
                    resultLength = bytesToRead;
                    _buffer.Retain();

                    _offset += bytesToRead;
                    _remaining -= bytesToRead;

                    if (_remaining == 0)
                    {
                        _buffer.Release();
                        _buffer = null;
                        _offset = 0;
                        _read.Release();
                        _canWrite.Release();
                    }
                    else
                    {
                        _canRead.Release();
                    }
                }
                else
                {
                    int toRead = _totalRemaining;
                    if (length != 0)
                    {
                        toRead = Math.Min(length, _totalRemaining);
                    }

                    var current = _slices[_segmentIndex];
                    int availableInCurrent = current.Length - _segmentOffset;
                    bool singleSegmentRead = toRead <= availableInCurrent;

                    if (singleSegmentRead)
                    {
                        int start = current.Offset + _segmentOffset;
                        chunk = new ReadOnlyMemory<byte>(current.Owner.Array, start, toRead);
                        resultBuffer = current.Owner;
                        resultOffset = start;
                        resultLength = toRead;
                        current.Owner.Retain();

                        _segmentOffset += toRead;
                        _totalRemaining -= toRead;
                        current.Advance(toRead);

                        if (_segmentOffset == current.Length)
                        {
                            _segmentIndex++;
                            _segmentOffset = 0;
                            if (_segmentIndex >= _slices.Count)
                            {
                                _slices = null;
                            }
                        }
                    }
                    else
                    {
                        PooledBuffer contiguous = PooledBuffer.Rent(toRead);
                        // copy into a contiguous buffer when spanning segments

                        int remainingToRead = toRead;
                        int written = 0;
                        while (remainingToRead > 0 && _slices != null)
                        {
                            var seg = _slices[_segmentIndex];
                            int available = seg.Length - _segmentOffset;
                            int take = Math.Min(remainingToRead, available);

                            new ReadOnlySpan<byte>(seg.Owner.Array, seg.Offset + _segmentOffset, take)
                                .CopyTo(contiguous.Span.Slice(written, take));

                            seg.Advance(take);
                            written += take;
                            remainingToRead -= take;
                            _segmentOffset += take;
                            _totalRemaining -= take;

                            if (_segmentOffset == seg.Length)
                            {
                                _segmentIndex++;
                                _segmentOffset = 0;
                                if (_segmentIndex >= _slices.Count)
                                {
                                    _slices = null;
                                }
                            }
                        }

                        chunk = new ReadOnlyMemory<byte>(contiguous.Array, 0, toRead);
                        ownedBuffer = contiguous;
                        resultBuffer = contiguous;
                        resultOffset = 0;
                        resultLength = toRead;
                    }

                    if (_totalRemaining == 0)
                    {
                        _slices = null;
                        _segmentIndex = 0;
                        _segmentOffset = 0;
                        _read.Release();
                        _canWrite.Release();
                    }
                    else
                    {
                        _canRead.Release();
                    }
                }

                _readLock.Release();
                return resultBuffer is null
                    ? ReadResult.Empty
                    : ReadResult.Ok(resultBuffer, resultOffset, resultLength);
            }
            catch (TaskCanceledException)
            {
                return ReadResult.Cancelled;
            }
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default)
        {
            if (slices.Length == 0)
            {
                return WriteSlicesAsync(Array.Empty<PooledBuffer.Slice>(), 0, token);
            }

            PooledBuffer.Slice[] rented = ArrayPool<PooledBuffer.Slice>.Shared.Rent(slices.Length);
            slices.CopyTo(rented);
            return WriteSlicesWithRentAsync(rented, slices.Length, token);
        }

        private async ValueTask<IOResult> WriteSlicesWithRentAsync(PooledBuffer.Slice[] rented, int count, CancellationToken token)
        {
            try
            {
                return await WriteSlicesAsync(rented, count, token);
            }
            finally
            {
                ArrayPool<PooledBuffer.Slice>.Shared.Return(rented, clearArray: true);
            }
        }

        private async ValueTask<IOResult> WriteSlicesAsync(PooledBuffer.Slice[] slices, int count, CancellationToken token)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    DisposeLeases(slices, count);
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                if (_buffer is not null || _slices is not null)
                {
                    DisposeLeases(slices, count);
                    _canWrite.Release();
                    return IOResult.InternalError;
                }

                if (count == 0)
                {
                    _canWrite.Release();
                    return IOResult.Ok;
                }

                _slices = new List<PooledBuffer.Slice>(count);
                _totalRemaining = 0;
                for (int i = 0; i < count; i++)
                {
                    PooledBuffer.Slice slice = slices[i];
                    if (slice.Length < 0 || slice.Offset < 0 || (uint)(slice.Offset + slice.Length) > (uint)slice.Owner.Length)
                    {
                        DisposeLeases(slices, count);
                        _slices = null;
                        _canWrite.Release();
                        return IOResult.InternalError;
                    }

                    _slices.Add(slice);
                    _totalRemaining += slice.Length;
                }

                _segmentIndex = 0;
                _segmentOffset = 0;

                _canRead.Release();
                await _read.WaitAsync(token);
                return IOResult.Ok;
            }
            catch (TaskCanceledException)
            {
                DisposeLeases(slices, count);
                return IOResult.Cancelled;
            }
        }

        private static void DisposeLeases(PooledBuffer.Slice[] slices, int count)
        {
            for (int i = 0; i < count; i++)
            {
                PooledBuffer.Slice slice = slices[i];
                slice.Dispose();
            }
        }

        public async ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                if (_buffer is not null)
                {
                    _canWrite.Release();
                    return IOResult.InternalError;
                }

                if (length == 0)
                {
                    _canWrite.Release();
                    return IOResult.Ok;
                }

                if (length < 0 || offset < 0 || (uint)(offset + length) > (uint)buffer.Length)
                {
                    _canWrite.Release();
                    return IOResult.InternalError;
                }

                buffer.Retain();
                _buffer = buffer;
                _offset = offset;
                _remaining = length;
                _slices = null;
                _totalRemaining = 0;
                _canRead.Release();
                await _read.WaitAsync(token);
                return IOResult.Ok;
            }
            catch (TaskCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

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
                _canRead.Release();
                _canWrite.Release();
                return IOResult.Ok;
            }
            catch (TaskCanceledException)
            {
                return IOResult.Cancelled;
            }
        }

        public async ValueTask<IOResult> CanReadAsync(CancellationToken token = default)
        {
            try
            {
                if (_eow)
                {
                    return IOResult.Ended;
                }
                await _readLock.WaitAsync(token);
                _readLock.Release();
                return !_eow ? IOResult.Ok : IOResult.Ended;
            }
            catch (TaskCanceledException)
            {
                return IOResult.Cancelled;
            }
        }
    }
}
