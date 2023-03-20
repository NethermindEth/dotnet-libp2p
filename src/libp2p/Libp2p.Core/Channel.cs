// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Utilities.Encoders;

[assembly: InternalsVisibleTo("Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Libp2p.Core.Tests")]

namespace Nethermind.Libp2p.Core;

// TODO: Rewrite using standard buffered channels

internal class Channel : IChannel
{
    private IChannel? _reversedChannel;
    private ILogger<Channel>? _logger;

    public Channel(ILoggerFactory ? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<Channel>();
        Id = "unknown";
        Reader = new ReaderWriter(_logger);
        Writer = new ReaderWriter(_logger);
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
                _logger = this._logger,
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

    public async Task CloseAsync(bool graceful = true)
    {
        State.Cancel();
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
        Reader = (ReaderWriter)parent.Writer;
        Writer = (ReaderWriter)parent.Reader;
        OnClose(async () => { (parent as Channel).State.Cancel(); });
        (parent as Channel).OnClose(async () => { State.Cancel(); });
    }

    internal class ReaderWriter : IReader, IWriter
    {
        private readonly ILogger? _logger;

        public ReaderWriter(ILogger ?logger = null)
        {
            _logger = logger;
        }
        private ReadOnlySequence<byte> _bytes;
        private readonly SemaphoreSlim _canWrite = new(1, 1);
        private readonly SemaphoreSlim _read = new(0, 1);
        private readonly SemaphoreSlim _canRead = new(1, 1);

        public async ValueTask<ReadOnlySequence<byte>> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (_bytes.Length == 0 && blockingMode == ReadBlockingMode.DontWait)
            {
                return new ReadOnlySequence<byte>();
            }
            await _canRead.WaitAsync(token);

            long bytesToRead = length != 0
                ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Length))
                : _bytes.Length;
            
            ReadOnlySequence<byte> chunk = default;
            do
            {
                ReadOnlySequence<byte> anotherChunk = default;
                
                if (_bytes.Length <= bytesToRead)
                {
                    anotherChunk = _bytes;
                    bytesToRead -= _bytes.Length;
                    _logger?.LogTrace("Read chunk {0} bytes: {1}", _bytes.Length, Encoding.UTF8.GetString(_bytes.ToArray()));
                    _bytes = default;
                    _read.Release();
                    _canWrite.Release();
                }
                else if (_bytes.Length > bytesToRead)
                {
                    anotherChunk = _bytes.Slice(0, bytesToRead);
                    _bytes = _bytes.Slice(bytesToRead, _bytes.End);
                    _logger?.LogTrace("Read enough {0} bytes: {1}", anotherChunk.Length, Encoding.UTF8.GetString(anotherChunk.ToArray()));
                    _canRead.Release();
                    bytesToRead = 0;
                }

                chunk = chunk.Length == 0 ? anotherChunk : chunk.Append(anotherChunk.First);
            } while (bytesToRead != 0);

            return chunk;
        }

        public async ValueTask WriteAsync(ReadOnlySequence<byte> bytes)
        {
            await _canWrite.WaitAsync();
            if (_bytes.Length != 0)
            {
                throw new InvalidProgramException();
            }
            
            _logger?.LogTrace("Write {0} bytes: {1}", bytes.Length, Encoding.UTF8.GetString(bytes.ToArray()));
            
            if (bytes.Length == 0)
            {
                _canWrite.Release();
                return;
            }
            
            _bytes = bytes;
            _canRead.Release();
            await _read.WaitAsync();
        }
    }
}
