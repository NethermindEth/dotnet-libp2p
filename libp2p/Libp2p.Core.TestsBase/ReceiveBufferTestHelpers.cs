// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.TestsBase;

/// <summary>Returns oversized storage and poisons it on return to expose borrowed receive memory.</summary>
public sealed class TrackingReceivePool : ArrayPool<byte>
{
    public sealed class Rental(int capacity)
    {
        public int Capacity { get; } = capacity;
        public byte[] Buffer { get; } = new byte[capacity + 257];
        public int ReturnCount;
    }

    private readonly ConcurrentQueue<Rental> _rentals = new();
    private readonly System.Threading.Channels.Channel<Rental> _rented = System.Threading.Channels.Channel.CreateUnbounded<Rental>();
    private readonly System.Threading.Channels.Channel<Rental> _returned = System.Threading.Channels.Channel.CreateUnbounded<Rental>();
    public Rental[] Rentals => _rentals.ToArray();

    public override byte[] Rent(int minimumLength)
    {
        Rental rental = new(minimumLength);
        Array.Fill(rental.Buffer, (byte)0xcc);
        _rentals.Enqueue(rental);
        _rented.Writer.TryWrite(rental);
        return rental.Buffer;
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        Rental rental = _rentals.Single(rental => ReferenceEquals(rental.Buffer, array));
        Interlocked.Increment(ref rental.ReturnCount);
        Array.Fill(array, (byte)0xdd);
        _returned.Writer.TryWrite(rental);
    }

    public ValueTask<Rental> WaitForRentAsync(CancellationToken token) => _rented.Reader.ReadAsync(token);
    public ValueTask<Rental> WaitForReturnAsync(CancellationToken token) => _returned.Reader.ReadAsync(token);

    public void AssertReturned(int count)
    {
        Assert.That(Rentals, Has.Length.EqualTo(count), "Each receive pump must rent just once.");
        foreach (Rental rental in Rentals)
        {
            Assert.That(rental.ReturnCount, Is.EqualTo(1), "Every rental must have exactly one return.");
        }
    }
}

/// <summary>Forwards to a real channel, reporting progress only after its reader acknowledges a write.</summary>
public sealed class ReceiveTestChannel : IChannel
{
    private readonly Channel _channel = new();
    private readonly System.Threading.Channels.Channel<int> _acknowledged = System.Threading.Channels.Channel.CreateUnbounded<int>();
    private readonly List<ReadOnlyMemory<byte>> _published = new();
    public IChannel Upper => _channel.Reverse;
    public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IOResult? Rejection { get; init; }
    public Exception? WriteFailure { get; init; }

    public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
    {
        foreach (ReadOnlyMemory<byte> memory in bytes) _published.Add(memory);
        WriteStarted.TrySetResult();
        if (WriteFailure is not null) throw WriteFailure;
        if (Rejection is { } rejection) return rejection;
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

    public async Task WaitForBytesAsync(int length, CancellationToken token)
    {
        int received = 0;
        while (received < length) received += await _acknowledged.Reader.ReadAsync(token);
        Assert.That(received, Is.EqualTo(length));
    }

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

            Assert.That(reading.IsCompleted, Is.False, "The WaitAll read must retain all earlier fragments.");
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

    public void AssertCompactAndStable(byte[] expected, TrackingReceivePool? pool = null)
    {
        HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
        foreach (ReadOnlyMemory<byte> memory in _published)
        {
            Assert.That(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment), Is.True);
            Assert.That(segment.Array, Has.Length.EqualTo(memory.Length), "Published fragments must own exact-size backing arrays.");
            Assert.That(segment.Offset, Is.Zero);
            Assert.That(arrays.Add(segment.Array!), Is.True, "Each publication must own independent storage.");
            if (pool is not null)
            {
                foreach (TrackingReceivePool.Rental rental in pool.Rentals)
                {
                    Assert.That(segment.Array, Is.Not.SameAs(rental.Buffer));
                    Assert.That(memory.Length, Is.LessThanOrEqualTo(rental.Capacity), "Reads must be limited to the requested capacity.");
                }
            }
        }
        Assert.That(_published.SelectMany(memory => memory.ToArray()).ToArray(), Is.EqualTo(expected),
            "Published bytes must survive later receives and return of poisoned scratch storage.");
    }

    public static byte[] Payload(int length, int seed = 0) => Enumerable.Range(0, length).Select(i => (byte)((i * 73 + seed) % 251)).ToArray();
}
