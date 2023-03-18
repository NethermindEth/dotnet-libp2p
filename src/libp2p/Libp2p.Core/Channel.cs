// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Libp2p.Core.TestsBase")]
[assembly: InternalsVisibleTo("Libp2p.Core.Tests")]

namespace Nethermind.Libp2p.Core;

// TODO: Rewrite using standard buffered channels

internal class Channel : IChannel
{
    private IChannel _reversedChannel;

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
            if (_reversedChannel != null)
            {
                return _reversedChannel;
            }

            Channel x = new((ReaderWriter)Writer, (ReaderWriter)Reader)
            {
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
        private ArraySegment<byte> _bytes;
        private ManualResetEventSlim _canWrite = new();
        private ManualResetEventSlim _read = new();
        private SemaphoreSlim _canRead = new(0, 1);

        public string Name { get; set; }


        internal class MemorySegment<T> : ReadOnlySequenceSegment<T>
        {
            public MemorySegment(ReadOnlyMemory<T> memory)
            {
                Memory = memory;
            }

            public MemorySegment<T> Append(ReadOnlyMemory<T> memory)
            {
                var segment = new MemorySegment<T>(memory)
                {
                    RunningIndex = RunningIndex + Memory.Length
                };

                Next = segment;

                return segment;
            }
        }

        public async ValueTask<ReadOnlySequence<byte>> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (_bytes.Count == 0 && blockingMode == ReadBlockingMode.DontWait)
            {
                return new ReadOnlySequence<byte>();
            }
            await _canRead.WaitAsync(token);

            int bytesToRead = length != 0
                ? (blockingMode == ReadBlockingMode.WaitAll ? length : Math.Min(length, _bytes.Count))
                : _bytes.Count;
            
            MemorySegment<byte>? chunk = null;
            MemorySegment<byte>? lchunk;
            do
            {
                ReadOnlyMemory<byte> anotherChunk = null;
                
                if (_bytes.Count <= bytesToRead)
                {
                    anotherChunk = _bytes;
                    bytesToRead -= _bytes.Count;
                    _bytes = null;
                    _read.Set();
                    _canWrite.Set();
                }
                else if (_bytes.Count > bytesToRead)
                {
                    anotherChunk = _bytes[..bytesToRead];
                    _bytes = _bytes[bytesToRead..];
                    bytesToRead = 0;
                }

                if (chunk == null)
                {
                    lchunk = chunk = new MemorySegment<byte>(anotherChunk);
                }
                else
                {
                    lchunk = chunk.Append(anotherChunk);
                }
            } while (bytesToRead != 0);

            _canRead.Release();
            return new ReadOnlySequence<byte>(chunk, 0, lchunk, lchunk.Memory.Length);
        }

        public ReaderWriter()
        {
            _canWrite.Set();
            _read.Reset();
        }

        public async ValueTask WriteAsync(ArraySegment<byte> bytes)
        {
            if (bytes.Count == 0)
            {
                return;
            }

            _canWrite.Wait();
            if (_bytes != null)
            {
                throw new InvalidProgramException();
            }

            _bytes = bytes;
            _canRead.Release();
            _read.Wait();
        }
    }
}
