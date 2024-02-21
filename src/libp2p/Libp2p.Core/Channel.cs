// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Tests")]
[assembly: InternalsVisibleTo("Nethermind.Libp2p.Core.Benchmarks")]

namespace Nethermind.Libp2p.Core;

internal class Channel : IChannel
{
    private IChannel? _reversedChannel;
    private ILogger<Channel>? _logger;

    public Channel(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<Channel>();
        Id = "unknown";
        Reader = new ReaderWriter(_logger);
        Writer = new ReaderWriter(_logger);
    }

    public Channel()
    {
        Id = "unknown";
        Reader = new ReaderWriter();
        Writer = new ReaderWriter();
    }

    private Channel(IReader reader, IWriter writer)
    {
        Id = "unknown";
        Reader = reader;
        Writer = writer;
    }

    private CancellationTokenSource State { get; init; } = new();

    public IChannel Reverse
    {
        get
        {
            if (_reversedChannel is not null)
            {
                return _reversedChannel;
            }

            Channel x = new((ReaderWriter)Writer, (ReaderWriter)Reader)
            {
                _logger = _logger,
                _reversedChannel = this,
                State = State,
                Id = Id + "-rev"
            };
            return _reversedChannel = x;
        }
    }

    public string Id { get; set; }
    public IReader Reader { get; private set; }
    public IWriter Writer { get; private set; }

    public bool IsClosed => State.IsCancellationRequested;

    public CancellationToken Token => State.Token;

    public Task CloseAsync(bool graceful = true)
    {
        State.Cancel();
        return Task.CompletedTask;
    }

    public void OnClose(Func<Task> action)
    {
        State.Token.Register(() => action().Wait());
    }

    public TaskAwaiter GetAwaiter()
    {
        return Task.Delay(-1, State.Token).ContinueWith(_ => { }, TaskContinuationOptions.OnlyOnCanceled).GetAwaiter();
    }

    public void Bind(IChannel parent)
    {
        Reader = (ReaderWriter)((Channel)parent).Writer;
        Writer = (ReaderWriter)((Channel)parent).Reader;
        Channel parentChannel = (Channel)parent;
        OnClose(() =>
        {
            parentChannel.State.Cancel();
            return Task.CompletedTask;
        });
        parentChannel.OnClose(() =>
        {
            State.Cancel();
            return Task.CompletedTask;
        });
    }

    internal class ReaderWriter : IReader, IWriter
    {
        private readonly ILogger? _logger;

        public ReaderWriter(ILogger? logger)
        {
            _logger = logger;
        }

        public ReaderWriter()
        {
        }

        private ReadOnlySequence<byte> _bytes;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly SemaphoreSlim _canRead = new(0, 1);
        private readonly SemaphoreSlim _readLock = new(1, 1);
        private bool _eof = false;

        public async ValueTask<ReadOnlySequence<byte>> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            await _readLock.WaitAsync(token);

            if (_eof)
            {
                _readLock.Release();
                throw new Exception("Can't read after EOF");
            }

            if (blockingMode == ReadBlockingMode.DontWait && _bytes.Length == 0)
            {
                _readLock.Release();
                return new ReadOnlySequence<byte>();
            }

            await _canRead.WaitAsync(token);

            if (_eof)
            {
                _canRead.Release();
                _readLock.Release();
                _read.Release();
                _canWrite.Release();
                throw new Exception("Can't read after EOF");
            }

            bool lockAgain = false;
            long bytesToRead = length != 0
                ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Length))
                : _bytes.Length;

            ReadOnlySequence<byte> chunk = default;
            do
            {
                if (lockAgain) await _canRead.WaitAsync(token);

                if (_eof)
                {
                    _canRead.Release();
                    _readLock.Release();
                    _read.Release();
                    throw new Exception("Can't read after EOF");
                }

                ReadOnlySequence<byte> anotherChunk = default;

                if (_bytes.Length <= bytesToRead)
                {
                    anotherChunk = _bytes;
                    bytesToRead -= _bytes.Length;
                    _logger?.ReadChunk(_bytes.Length);
                    _bytes = default;
                    _read.Release();
                    _canWrite.Release();
                }
                else if (_bytes.Length > bytesToRead)
                {
                    anotherChunk = _bytes.Slice(0, bytesToRead);
                    _bytes = _bytes.Slice(bytesToRead, _bytes.End);
                    _logger?.ReadEnough(_bytes.Length);
                    bytesToRead = 0;
                    _canRead.Release();
                }

                chunk = chunk.Length == 0 ? anotherChunk : chunk.Append(anotherChunk.First);
                lockAgain = true;
            } while (bytesToRead != 0);

            _readLock.Release();
            return chunk;
        }

        public async ValueTask WriteAsync(ReadOnlySequence<byte> bytes)
        {
            await _canWrite.WaitAsync();

            if (_eof)
            {
                _canWrite.Release();
                throw new Exception("Can't write after EOF");
            }

            if (_bytes.Length != 0)
            {
                throw new Exception("Channel is not properly locked");
            }

            _logger?.WriteBytes(bytes.Length);

            if (bytes.Length == 0)
            {
                _canWrite.Release();
                return;
            }

            _bytes = bytes;
            _canRead.Release();
            await _read.WaitAsync();
        }

        public async ValueTask WriteEofAsync()
        {
            await _canWrite.WaitAsync();
            _eof = true;
            _canRead.Release();
            _canWrite.Release();
        }

        public async ValueTask<bool> CanReadAsync(CancellationToken token = default)
        {
            if (_eof)
            {
                return false;
            }
            await _readLock.WaitAsync(token);
            _readLock.Release();
            return !_eof;
        }
    }

    public ValueTask<ReadOnlySequence<byte>> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
        CancellationToken token = default)
    {
        return Reader.ReadAsync(length, blockingMode, token);
    }

    public ValueTask WriteAsync(ReadOnlySequence<byte> bytes)
    {
        return Writer.WriteAsync(bytes);
    }

    public ValueTask WriteEofAsync() => Writer.WriteEofAsync();

    public ValueTask<bool> CanReadAsync(CancellationToken token = default) => Reader.CanReadAsync(token);
}
