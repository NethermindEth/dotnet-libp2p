// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>Observes transport publications while preserving real channel backpressure.</summary>
public sealed class ReceiveTestChannel : IChannel
{
    private readonly Channel _channel = new();
    private readonly System.Threading.Channels.Channel<int> _acknowledged = System.Threading.Channels.Channel.CreateUnbounded<int>();
    private readonly List<ReadOnlyMemory<byte>> _published = new();
    public IChannel Upper => _channel.Reverse;

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        foreach (ReadOnlyMemory<byte> memory in bytes) _published.Add(memory);
        IOResult result = await _channel.WriteAsync(bytes, token);
        if (result == IOResult.Ok) _acknowledged.Writer.TryWrite(checked((int)bytes.Length));
        return result;
    }

    public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        => _channel.ReadAsync(length, blockingMode, token);
    public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _channel.WriteEofAsync(token);
    public ValueTask CloseAsync() => _channel.CloseAsync();
    public ValueTask AbortAsync() => _channel.AbortAsync();
    public TaskAwaiter GetAwaiter() => _channel.GetAwaiter();

    public async Task<ReadOnlySequence<byte>> ReadFragmentsAsync(
        Func<ReadOnlyMemory<byte>, Task> send, byte[] expected, int[] fragmentLengths, CancellationToken token)
    {
        Assert.That(fragmentLengths.Sum(), Is.EqualTo(expected.Length - 1));
        Task<ReadResult> reading = Upper.ReadAsync(expected.Length, ReadBlockingMode.WaitAll, token).AsTask();
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
            Assert.That(result.Data.ToArray(), Is.EqualTo(expected));
            return result.Data;
        }
        finally
        {
            if (!reading.IsCompleted) await CloseAsync();
            await reading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private async Task WaitForBytesAsync(int length, CancellationToken token)
    {
        int received = 0;
        while (received < length) received += await _acknowledged.Reader.ReadAsync(token);
        Assert.That(received, Is.EqualTo(length));
    }

    public void AssertCompactAndStable(byte[] expected)
    {
        HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
        foreach (ReadOnlyMemory<byte> memory in _published)
        {
            Assert.That(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment), Is.True);
            arrays.Add(segment.Array!);
        }

        Assert.That(arrays, Is.Not.Empty);
        long capacity = arrays.Sum(array => (long)array.Length);
        Assert.That(capacity, Is.LessThan(expected.LongLength + arrays.First().Length),
            "Retained storage must fit the payload plus at most one partially filled block.");
        Assert.That(arrays.Count, Is.LessThan(_published.Count),
            "Small fragments must share blocks rather than allocate one backing array per receive.");
        Assert.That(_published.SelectMany(memory => memory.ToArray()).ToArray(), Is.EqualTo(expected),
            "Published bytes must survive later receives and transport shutdown.");
    }

    public static byte[] Payload(int length, int seed = 0) => Enumerable.Range(0, length).Select(i => (byte)((i * 73 + seed) % 251)).ToArray();
}
