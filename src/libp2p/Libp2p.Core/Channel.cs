// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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

    private class ReaderWriter : IReader, IWriter
    {
        private readonly byte[] iq = new byte[1024 * 1024];
        private readonly SemaphoreSlim lk = new(0);
        private int rPtr;
        private int wPtr;
        public string Name { get; set; }

        public async Task<int> ReadAsync(byte[] bytes, bool blocking = true, CancellationToken token = default)
        {
            int len = bytes.Length;

            int restSize = bytes.Length;
            while (restSize != 0)
            {
                int sizeAvailable = wPtr - rPtr;
                if (sizeAvailable > 0)
                {
                    if (sizeAvailable < restSize)
                    {
                        Array.ConstrainedCopy(iq, rPtr, bytes, bytes.Length - restSize, sizeAvailable);
                        restSize -= sizeAvailable;
                        rPtr += sizeAvailable;
                        if (!blocking)
                        {
                            return len - restSize;
                        }

                        await lk.WaitAsync(token);
                        sizeAvailable = wPtr - rPtr;
                    }
                    else
                    {
                        Array.ConstrainedCopy(iq, rPtr, bytes, len - restSize, restSize);
                        rPtr += restSize;
                        return len;
                    }
                }
                else
                {
                    await lk.WaitAsync(token);
                }
            }

            return len;
        }

        public async Task WriteAsync(byte[] bytes)
        {
            Array.ConstrainedCopy(bytes, 0, iq, wPtr, bytes.Length);
            int prev = wPtr;
            wPtr += bytes.Length;
            if (prev == rPtr)
            {
                lk.Release();
            }
        }
    }
}
