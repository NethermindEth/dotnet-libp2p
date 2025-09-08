// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;

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

        private ReadOnlySequence<byte> _bytes;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly SemaphoreSlim _canRead = new(0, 1);
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private readonly Channel? _externalCompletionMonitor;
        internal bool _eow = false;

        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            try
            {
                await _readLock.WaitAsync(token);

                if (_eow)
                {
                    _readLock.Release();
                    return ReadResult.Ended;
                }

                if (blockingMode == ReadBlockingMode.DontWait && _bytes.Length == 0)
                {
                    _readLock.Release();
                    return ReadResult.Empty;
                }

                await _canRead.WaitAsync(token);

                if (_eow)
                {
                    _canRead.Release();
                    _readLock.Release();
                    _read.Release();
                    return ReadResult.Ended;
                }

                bool lockAgain = false;
                long bytesToRead = length != 0
                    ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Length))
                    : _bytes.Length;

                ReadOnlySequence<byte> chunk = default;
                do
                {
                    if (lockAgain) await _canRead.WaitAsync(token);

                    if (_eow)
                    {
                        _canRead.Release();
                        _readLock.Release();
                        _read.Release();
                        return ReadResult.Ended;
                    }

                    ReadOnlySequence<byte> anotherChunk = default;

                    if (_bytes.Length <= bytesToRead)
                    {
                        anotherChunk = _bytes;
                        bytesToRead -= _bytes.Length;
                        _bytes = default;
                        _read.Release();
                        _canWrite.Release();
                    }
                    else if (_bytes.Length > bytesToRead)
                    {
                        anotherChunk = _bytes.Slice(0, bytesToRead);
                        _bytes = _bytes.Slice(bytesToRead, _bytes.End);
                        bytesToRead = 0;
                        _canRead.Release();
                    }

                    chunk = chunk.Length == 0 ? anotherChunk : chunk.Append(anotherChunk.First);
                    lockAgain = true;
                } while (bytesToRead != 0);

                _readLock.Release();
                return ReadResult.Ok(chunk);
            }
            catch (TaskCanceledException)
            {
                return ReadResult.Cancelled;
            }
        }

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            try
            {
                await _canWrite.WaitAsync(token);

                if (_eow)
                {
                    _canWrite.Release();
                    return IOResult.Ended;
                }

                if (_bytes.Length != 0)
                {
                    return IOResult.InternalError;
                }

                if (bytes.Length == 0)
                {
                    _canWrite.Release();
                    return IOResult.Ok;
                }

                _bytes = bytes;
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
