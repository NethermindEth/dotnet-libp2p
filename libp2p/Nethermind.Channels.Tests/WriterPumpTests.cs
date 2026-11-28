// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.InteropServices;

namespace Nethermind.Channels.Tests;

public class WriterPumpTests
{
    [TestCase(8, 16)]
    [TestCase(8, 17)]
    [TestCase(64, 513)]
    public async Task FragmentedWaitAllUsesBoundedReceiveStorageAndOwnsItsResult(int capacity, int length)
    {
        Channel channel = new();
        TrackingPool pool = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        byte[] expected = Enumerable.Range(0, length).Select(i => (byte)(i * 37 % 251)).ToArray();
        int offset = 0;
        Task<ReadResult> reading = channel.Reverse.ReadAsync(length, token: deadline.Token).AsTask();
        try
        {
            IOResult pumped = await WriterExtensions.WriteFromAsync(channel, (memory, token) =>
            {
                Assert.That(token, Is.EqualTo(deadline.Token));
                Assert.That(memory.Length, Is.InRange(1, capacity));
                memory.Span.Fill(0xee);
                if (offset == expected.Length) return ValueTask.FromResult(0);
                memory.Span[0] = expected[offset++];
                return ValueTask.FromResult(1);
            }, capacity, pool, deadline.Token);
            using ReadResult read = await reading;
            Assert.That(pumped, Is.EqualTo(IOResult.Ok));
            Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(read.IsOwned, Is.True);
            Assert.That(read.Sequence.ToArray(), Is.EqualTo(expected));
            HashSet<byte[]> backing = new(ReferenceEqualityComparer.Instance);
            foreach (ReadOnlyMemory<byte> memory in read.Sequence)
            {
                Assert.That(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment), Is.True);
                backing.Add(segment.Array!);
            }
            int maximumBlocks = (length + capacity - 1) / capacity;
            Assert.That(backing.Count, Is.LessThanOrEqualTo(maximumBlocks));
            Assert.That(pool.Rentals.Count, Is.LessThanOrEqualTo(maximumBlocks + 1),
                "Receiving must not rent a whole buffer per fragment, including while WaitAll accumulates.");
            foreach (TrackingPool.Rental rental in pool.Rentals)
            {
                Assert.That(rental.ReturnCount, Is.EqualTo(backing.Contains(rental.Buffer) ? 0 : 1));
            }
            read.Dispose();
            pool.AssertReturned();
        }
        finally
        {
            await channel.CloseAsync();
            (await reading).Dispose();
        }
    }

    [Test]
    public async Task ReleasedReadsAllowTheSameBufferToBeReused()
    {
        Channel channel = new();
        using GatedWriter writer = new(channel);
        TrackingPool pool = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        int calls = 0;
        Task<IOResult> pumping = WriterExtensions.WriteFromAsync(writer, (memory, _) =>
        {
            Assert.That(memory.Length, Is.EqualTo(8), "The sole root owner can reuse the entire buffer.");
            memory.Span.Fill((byte)++calls);
            return ValueTask.FromResult(calls <= 3 ? 2 : 0);
        }, 8, pool, deadline.Token).AsTask();
        try
        {
            for (int i = 1; i <= 3; i++)
            {
                ReadResult read = await channel.Reverse.ReadAsync(2, token: deadline.Token);
                await writer.WaitForWriteAsync(deadline.Token);
                Assert.That(read.Sequence.ToArray(), Is.EqualTo(new byte[] { (byte)i, (byte)i }));
                read.Dispose();
                writer.Continue();
            }
            Assert.That(await pumping, Is.EqualTo(IOResult.Ok));
            Assert.That(pool.Rentals, Has.Count.EqualTo(1));
            pool.AssertReturned();
            // Source EOF leaves transport half-close decisions to the caller.
            Task<IOResult> next = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 42 }), deadline.Token).AsTask();
            using ReadResult subsequent = await channel.Reverse.ReadAsync(1, token: deadline.Token);
            Assert.That(subsequent.Sequence.ToArray(), Is.EqualTo(new byte[] { 42 }));
            Assert.That(await next, Is.EqualTo(IOResult.Ok));
        }
        finally
        {
            deadline.Cancel();
            await channel.CloseAsync();
        }
    }

    [Test]
    public async Task RetainedReadSurvivesShortTailsRolloverAndReuseOfLaterBuffers()
    {
        Channel channel = new();
        using GatedWriter writer = new(channel);
        TrackingPool pool = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        int calls = 0;
        int[] sizes = [8, 7, 8, 8];
        Task<IOResult> pumping = WriterExtensions.WriteFromAsync(writer, (memory, _) =>
        {
            Assert.That(memory.Length, Is.EqualTo(sizes[calls]));
            memory.Span.Fill((byte)(11 * ++calls));
            return ValueTask.FromResult(calls switch { 1 => 1, 2 => 7, 3 => 8, _ => 0 });
        }, 8, pool, deadline.Token).AsTask();
        ReadResult retained = default;
        try
        {
            retained = await channel.Reverse.ReadAsync(1, token: deadline.Token);
            await writer.WaitForWriteAsync(deadline.Token);
            writer.Continue();
            ReadResult tail = await channel.Reverse.ReadAsync(7, token: deadline.Token);
            await writer.WaitForWriteAsync(deadline.Token);
            Assert.That(tail.Sequence.ToArray(), Is.EqualTo(Enumerable.Repeat((byte)22, 7)));
            tail.Dispose();
            writer.Continue();
            ReadResult later = await channel.Reverse.ReadAsync(8, token: deadline.Token);
            await writer.WaitForWriteAsync(deadline.Token);
            Assert.That(later.Sequence.ToArray(), Is.EqualTo(Enumerable.Repeat((byte)33, 8)));
            later.Dispose();
            writer.Continue();
            Assert.That(await pumping, Is.EqualTo(IOResult.Ok));
            Assert.That(pool.Rentals, Has.Count.EqualTo(2));
            Assert.That(pool.Rentals[0].ReturnCount, Is.Zero);
            Assert.That(pool.Rentals[1].ReturnCount, Is.EqualTo(1));
            Assert.That(retained.Sequence.ToArray(), Is.EqualTo(new byte[] { 11 }));
            retained.Dispose();
            pool.AssertReturned();
        }
        finally
        {
            retained.Dispose();
            deadline.Cancel();
            await channel.CloseAsync();
        }
    }

    [Test]
    public async Task ForwardingKeepsTheReceiveBufferAliveAfterThePumpAndOriginalReadEnd()
    {
        Channel source = new();
        Channel destination = new();
        TrackingPool pool = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        byte[] expected = [3, 5, 7, 11];
        int calls = 0;
        Task<IOResult> pumping = WriterExtensions.WriteFromAsync(source, (memory, _) =>
        {
            if (++calls > 1) return ValueTask.FromResult(0);
            expected.CopyTo(memory);
            return ValueTask.FromResult(expected.Length);
        }, 8, pool, deadline.Token).AsTask();
        try
        {
            using ReadResult read = await source.Reverse.ReadAsync(4, token: deadline.Token);
            using PooledBuffer.Slice forwarding = read.ToSlice();
            Task<IOResult> writing = destination.WriteAsync(forwarding, deadline.Token).AsTask();
            read.Dispose();
            forwarding.Dispose();
            Assert.That(await pumping, Is.EqualTo(IOResult.Ok));
            Assert.That(pool.Rentals.Single().ReturnCount, Is.Zero);
            using ReadResult forwarded = await destination.Reverse.ReadAsync(4, token: deadline.Token);
            Assert.That(await writing, Is.EqualTo(IOResult.Ok));
            Assert.That(forwarded.Sequence.ToArray(), Is.EqualTo(expected));
            forwarded.Dispose();
            pool.AssertReturned();
        }
        finally
        {
            deadline.Cancel();
            await source.CloseAsync();
            await destination.CloseAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SourceFailureReleasesThePumpButPreservesACompletedRead(bool cancel)
    {
        Channel channel = new();
        TrackingPool pool = new();
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException failure = new("Receive failed");
        int calls = 0;
        Task<IOResult> pumping = WriterExtensions.WriteFromAsync(channel, async (memory, token) =>
        {
            memory.Span.Fill(0xee);
            if (++calls == 1)
            {
                memory.Span[0] = 42;
                return 1;
            }
            receiving.TrySetResult();
            await resume.Task.WaitAsync(token);
            throw failure;
        }, 8, pool, cancellation.Token).AsTask();
        try
        {
            using ReadResult read = await channel.Reverse.ReadAsync(1, token: deadline.Token);
            await receiving.Task.WaitAsync(deadline.Token);
            if (cancel) cancellation.Cancel();
            else resume.TrySetResult();
            if (cancel) Assert.CatchAsync<OperationCanceledException>(async () => await pumping.WaitAsync(deadline.Token));
            else Assert.That(Assert.ThrowsAsync<IOException>(async () => await pumping.WaitAsync(deadline.Token)), Is.SameAs(failure));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(pool.Rentals.Single().ReturnCount, Is.Zero);
            Assert.That(read.Sequence.ToArray(), Is.EqualTo(new byte[] { 42 }));
            read.Dispose();
            pool.AssertReturned();
        }
        finally
        {
            cancellation.Cancel();
            await channel.CloseAsync();
        }
    }

    [TestCase(IOResult.Ended)]
    [TestCase(IOResult.Aborted)]
    [TestCase(IOResult.Cancelled)]
    [TestCase(IOResult.InternalError)]
    public async Task RejectedWriteStopsReceivingAndReturnsItsBuffer(IOResult rejection)
    {
        RejectingWriter writer = new(rejection);
        TrackingPool pool = new();
        int calls = 0;
        IOResult result = await WriterExtensions.WriteFromAsync(writer, (memory, _) =>
        {
            calls++;
            memory.Span[0] = 42;
            return ValueTask.FromResult(1);
        }, 8, pool);
        Assert.That(result, Is.EqualTo(rejection));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(writer.WriteCount, Is.EqualTo(1));
        pool.AssertReturned();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingWriteCancellationOrAbortReturnsEveryLink(bool cancel)
    {
        Channel channel = new();
        TrackingPool pool = new();
        using CancellationTokenSource cancellation = new();
        int calls = 0;
        Task<IOResult> pumping = WriterExtensions.WriteFromAsync(channel, (memory, _) =>
        {
            calls++;
            memory.Span[0] = 42;
            return ValueTask.FromResult(1);
        }, 8, pool, cancellation.Token).AsTask();
        try
        {
            Assert.That(pumping.IsCompleted, Is.False);
            if (cancel) cancellation.Cancel();
            else await channel.AbortAsync();
            Assert.That(await pumping.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(cancel ? IOResult.Cancelled : IOResult.Aborted));
            Assert.That(calls, Is.EqualTo(1));
            pool.AssertReturned();
        }
        finally
        {
            cancellation.Cancel();
            await channel.CloseAsync();
        }
    }

    [Test]
    public void ThrowingWriterReturnsTheReceiveBufferAndStopsReceiving()
    {
        IOException failure = new("Write failed");
        RejectingWriter writer = new(IOResult.Ok) { Failure = failure };
        TrackingPool pool = new();
        int calls = 0;
        Assert.That(Assert.ThrowsAsync<IOException>(async () => await WriterExtensions.WriteFromAsync(writer, (memory, _) =>
        {
            calls++;
            memory.Span[0] = 42;
            return ValueTask.FromResult(1);
        }, 8, pool)), Is.SameAs(failure));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(writer.WriteCount, Is.EqualTo(1));
        pool.AssertReturned();
    }

    [TestCase(-1)]
    [TestCase(9)]
    public void InvalidReceiveCountReturnsTheBufferWithoutPublishing(int length)
    {
        RejectingWriter writer = new(IOResult.Ok);
        TrackingPool pool = new();
        int calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(async () => await WriterExtensions.WriteFromAsync(writer, (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(length);
        }, 8, pool));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(writer.WriteCount, Is.Zero);
        pool.AssertReturned();
    }

    [Test]
    public async Task EmptySourceReturnsItsBufferWithoutWritingOrSendingEof()
    {
        RejectingWriter writer = new(IOResult.Ok);
        TrackingPool pool = new();
        Assert.That(await WriterExtensions.WriteFromAsync(writer, (_, _) => ValueTask.FromResult(0), 8, pool), Is.EqualTo(IOResult.Ok));
        Assert.That(writer.WriteCount, Is.Zero);
        pool.AssertReturned();
    }

    [Test]
    public void AlreadyCancelledPumpDoesNotRentOrReceive()
    {
        RejectingWriter writer = new(IOResult.Ok);
        TrackingPool pool = new();
        Assert.CatchAsync<OperationCanceledException>(async () => await WriterExtensions.WriteFromAsync(writer,
            (_, _) => throw new AssertionException("The source must not be called."), 8, pool, new CancellationToken(true)));
        Assert.That(pool.Rentals, Is.Empty);
        Assert.That(writer.WriteCount, Is.Zero);
    }

    private sealed class GatedWriter(IWriter writer) : IWriter, IDisposable
    {
        private readonly System.Threading.Channels.Channel<int> _written = System.Threading.Channels.Channel.CreateUnbounded<int>();
        private readonly SemaphoreSlim _continue = new(0);
        public ValueTask<int> WaitForWriteAsync(CancellationToken token) => _written.Reader.ReadAsync(token);
        public void Continue() => _continue.Release();
        public void Dispose() => _continue.Dispose();

        public async ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        {
            IOResult result = await writer.WriteAsync(buffer, length, offset, token);
            if (result == IOResult.Ok)
            {
                _written.Writer.TryWrite(length);
                await _continue.WaitAsync(token);
            }
            return result;
        }

        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => writer.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default) => writer.WriteAsync(slice, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default) => writer.WriteAsync(slices, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => throw new AssertionException("The pump must not send EOF.");
    }

    private sealed class RejectingWriter(IOResult result) : IWriter
    {
        public int WriteCount { get; private set; }
        public Exception? Failure { get; init; }
        public ValueTask<IOResult> WriteAsync(PooledBuffer buffer, int length, int offset = 0, CancellationToken token = default)
        {
            WriteCount++;
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(result);
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => throw new AssertionException("The pump must publish an owned buffer.");
        public ValueTask<IOResult> WriteAsync(PooledBuffer.Slice slice, CancellationToken token = default) => throw new AssertionException("The pump must publish an owned buffer.");
        public ValueTask<IOResult> WriteAsync(ReadOnlySpan<PooledBuffer.Slice> slices, CancellationToken token = default) => throw new AssertionException("The pump must publish an owned buffer.");
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => throw new AssertionException("The pump must not send EOF.");
    }

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public sealed class Rental(int capacity)
        {
            public byte[] Buffer { get; } = new byte[capacity + 13];
            public int ReturnCount;
        }

        public List<Rental> Rentals { get; } = new();
        public override byte[] Rent(int minimumLength)
        {
            Rental rental = new(minimumLength);
            Rentals.Add(rental);
            return rental.Buffer;
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Rental rental = Rentals.Single(rental => ReferenceEquals(rental.Buffer, array));
            Interlocked.Increment(ref rental.ReturnCount);
            Array.Fill(array, (byte)0xdd);
        }
        public void AssertReturned()
        {
            Assert.That(Rentals, Is.Not.Empty);
            foreach (Rental rental in Rentals) Assert.That(rental.ReturnCount, Is.EqualTo(1));
        }
    }
}
