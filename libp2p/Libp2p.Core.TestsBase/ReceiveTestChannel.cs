// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Channels;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>Observes transport publications using the real channel's write acknowledgements.</summary>
public sealed class ReceiveTestChannel : IChannel
{
    private readonly Channel _channel = new();
    private readonly System.Threading.Channels.Channel<int> _acknowledged = System.Threading.Channels.Channel.CreateUnbounded<int>();
    private readonly HashSet<PooledBuffer> _receiveBuffers = new(ReferenceEqualityComparer.Instance);
    private int _publications;
    public IChannel Upper => _channel.Reverse;

    public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        => throw new AssertionException("The receive pump must publish an owned buffer.");

    public async ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
    {
        _receiveBuffers.Add(buffer);
        _publications++;
        IOResult result = await _channel.WriteAsync(buffer, length, offset, token);
        if (result == IOResult.Ok) _acknowledged.Writer.TryWrite(length);
        return result;
    }

    public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default)
        => _channel.WriteAsync(slice, token);

    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        => _channel.ReadAsync(length, blockingMode, token);
    public ValueTask<ReadResult> ReadOwnedAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        => _channel.ReadOwnedAsync(length, blockingMode, token);
    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _channel.WriteEofAsync(token);
    public ValueTask CloseAsync() => _channel.CloseAsync();
    public ValueTask AbortAsync() => _channel.AbortAsync();
    public TaskAwaiter GetAwaiter() => _channel.GetAwaiter();

    public async Task<ReadResult> ReadFragmentsAsync(
        Func<ReadOnlyMemory<byte>, Task> send, byte[] expected, int[] fragmentLengths, CancellationToken token)
    {
        Assert.That(fragmentLengths.Sum(), Is.EqualTo(expected.Length - 1));
        Task<ReadResult> reading = Upper.ReadOwnedAsync(expected.Length, ReadBlockingMode.WaitAll, token).AsTask();
        bool transferred = false;
        try
        {
            int offset = 0;
            foreach (int length in fragmentLengths)
            {
                await send(expected.AsMemory(offset, length));
                await WaitForBytesAsync(length, token);
                offset += length;
            }

            Assert.That(reading.IsCompleted, Is.False, "WaitAll must retain all earlier fragments.");
            await send(expected.AsMemory(offset, 1));
            ReadResult result = await reading.WaitAsync(token);
            await WaitForBytesAsync(1, token);
            Assert.That(result.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(result.IsOwned, Is.True, "The read must exercise the pooled ownership path.");
            Assert.That(result.Data.ToArray(), Is.EqualTo(expected));
            transferred = true;
            return result;
        }
        finally
        {
            if (!reading.IsCompleted) await CloseAsync();
            if (!transferred)
            {
                using ReadResult unused = await reading.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private async Task WaitForBytesAsync(int length, CancellationToken token)
    {
        int received = 0;
        while (received < length) received += await _acknowledged.Reader.ReadAsync(token);
        Assert.That(received, Is.EqualTo(length));
    }

    public void AssertReceiveBuffersReused()
    {
        Assert.That(_receiveBuffers, Is.Not.Empty);
        Assert.That(_receiveBuffers.Count, Is.LessThan(_publications),
            "Small fragments must reuse receive buffers rather than rent one per publication.");
    }

    public static void AssertCompactAndStable(ReadResult result, byte[] expected)
    {
        Assert.That(result.IsOwned, Is.True, "The read must exercise the pooled ownership path.");
        HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
        foreach (ReadOnlyMemory<byte> memory in result.Data)
        {
            Assert.That(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment), Is.True);
            arrays.Add(segment.Array!);
        }

        Assert.That(arrays, Is.Not.Empty);
        long capacity = arrays.Sum(array => (long)array.Length);
        Assert.That(capacity, Is.LessThanOrEqualTo(Math.Max(16, expected.LongLength * 4)),
            "Fragmented reads must retain storage proportional to payload, including geometric growth and pool bucket rounding.");
        Assert.That(result.Data.ToArray(), Is.EqualTo(expected),
            "Owned read bytes must survive later receives and transport shutdown.");
    }

    public static byte[] Payload(int length, int seed = 0) => Enumerable.Range(0, length).Select(i => (byte)((i * 73 + seed) % 251)).ToArray();
}
