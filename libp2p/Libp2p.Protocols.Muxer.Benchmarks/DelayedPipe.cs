// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Protocols.Muxer.Benchmarks;

/// <summary>
/// Buffered in-memory link where every write reaches the other end after a fixed one-way delay,
/// like a network path. Writes complete at once.
/// </summary>
internal static class DelayedPipe
{
    public static (End Left, End Right) Create(TimeSpan oneWayDelay)
    {
        Channel<(long, byte[])> leftToRight = System.Threading.Channels.Channel.CreateUnbounded<(long, byte[])>();
        Channel<(long, byte[])> rightToLeft = System.Threading.Channels.Channel.CreateUnbounded<(long, byte[])>();
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long delayTicks = (long)(oneWayDelay.TotalSeconds * TimeProvider.System.TimestampFrequency);
        return (new End(rightToLeft.Reader, leftToRight.Writer, delayTicks, closed),
            new End(leftToRight.Reader, rightToLeft.Writer, delayTicks, closed));
    }

    internal sealed class End(
        ChannelReader<(long DueTimestamp, byte[] Data)> inbound,
        ChannelWriter<(long DueTimestamp, byte[] Data)> outbound,
        long delayTicks,
        TaskCompletionSource closed) : IChannel
    {
        private ReadOnlyMemory<byte> _pending;

        public CancellationToken CancellationToken => CancellationToken.None;

        public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll,
            CancellationToken token = default)
        {
            if (length == 0 && blockingMode == ReadBlockingMode.WaitAll)
                return new ReadResult { Result = IOResult.Ok, Data = default };

            if (length == 0)
            {
                if (_pending.IsEmpty && !await ReceiveAsync(token)) return ReadResult.Ended;
                ReadOnlyMemory<byte> all = _pending;
                _pending = default;
                return new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(all) };
            }

            byte[] result = new byte[length];
            int filled = 0;
            while (filled == 0 || (blockingMode == ReadBlockingMode.WaitAll && filled < length))
            {
                if (_pending.IsEmpty && !await ReceiveAsync(token)) return ReadResult.Ended;
                int take = Math.Min(_pending.Length, length - filled);
                _pending[..take].CopyTo(result.AsMemory(filled));
                _pending = _pending[take..];
                filled += take;
            }

            return new ReadResult { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(result, 0, filled) };
        }

        private async ValueTask<bool> ReceiveAsync(CancellationToken token)
        {
            if (!await inbound.WaitToReadAsync(token) || !inbound.TryRead(out (long DueTimestamp, byte[] Data) chunk))
                return false;
            TimeSpan wait = TimeProvider.System.GetElapsedTime(TimeProvider.System.GetTimestamp(), chunk.DueTimestamp);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
            _pending = chunk.Data;
            return true;
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) =>
            ValueTask.FromResult(outbound.TryWrite((TimeProvider.System.GetTimestamp() + delayTicks, bytes.ToArray()))
                ? IOResult.Ok
                : IOResult.Ended);

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            outbound.TryComplete();
            return ValueTask.FromResult(IOResult.Ok);
        }

        public ValueTask CloseAsync()
        {
            outbound.TryComplete();
            closed.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync() => CloseAsync();

        public TaskAwaiter GetAwaiter() => closed.Task.GetAwaiter();
    }
}
