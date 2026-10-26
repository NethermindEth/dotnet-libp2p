// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.InteropServices;

namespace Nethermind.Libp2p.Core.Tests;

public class WriterReceiveTests
{
    [TestCase(1, 1)]
    [TestCase(8, 1)]
    [TestCase(8, 5)]
    [TestCase(1024, 1)]
    [TestCase(1024, 2048)]
    public async Task FragmentedReadSharesStorageAndSurvivesLaterReceives(int capacity, int fragmentSize)
    {
        Channel channel = new();
        IWriter writer = channel;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        byte[] expected = Enumerable.Range(0, capacity * 2 + 3).Select(i => (byte)(i % 251)).ToArray();
        int offset = 0;
        Task<ReadResult> reading = channel.Reverse.ReadAsync(expected.Length, token: deadline.Token).AsTask();
        try
        {
            IOResult result = await writer.WriteFromAsync((memory, token) =>
            {
                Assert.That(token, Is.EqualTo(deadline.Token));
                Assert.That(memory.Length, Is.InRange(1, capacity));
                // A source can fill its entire destination. Previously published regions must be excluded.
                memory.Span.Fill(0xee);
                int length = Math.Min(Math.Min(fragmentSize, memory.Length), expected.Length - offset);
                expected.AsMemory(offset, length).CopyTo(memory);
                offset += length;
                return ValueTask.FromResult(length);
            }, capacity, deadline.Token);
            Assert.That(result, Is.EqualTo(IOResult.Ok));
            ReadResult read = await reading;
            Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(read.Data.ToArray(), Is.EqualTo(expected));

            HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
            foreach (ReadOnlyMemory<byte> memory in read.Data)
            {
                Assert.That(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment), Is.True);
                arrays.Add(segment.Array!);
            }
            Assert.That(arrays.Sum(array => array.Length), Is.LessThan(expected.Length + capacity),
                "Backing storage must grow with payload bytes, not the number of receive fragments.");

            // EOF in the source must not close the channel; another pump can write to it.
            Task<IOResult> laterWrite = writer.WriteFromAsync((memory, _) =>
            {
                if (offset++ > expected.Length) return ValueTask.FromResult(0);
                memory.Span.Fill(0xff);
                return ValueTask.FromResult(memory.Length);
            }, capacity, deadline.Token).AsTask();
            ReadResult laterRead = await channel.Reverse.ReadAsync(capacity, token: deadline.Token);
            Assert.That(laterRead.Data.ToArray(), Is.All.EqualTo((byte)0xff));
            Assert.That(await laterWrite, Is.EqualTo(IOResult.Ok));
            await channel.CloseAsync();
            Assert.That(read.Data.ToArray(), Is.EqualTo(expected));
        }
        finally
        {
            await channel.CloseAsync();
            await reading;
        }
    }

    [Test]
    public async Task LaterReadCanRetainBothBoundaryBuffers()
    {
        const int capacity = 8;
        Channel channel = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        int offset = 0;
        Task<IOResult> pumping = ((IWriter)channel).WriteFromAsync((memory, _) =>
        {
            if (offset == capacity * 2) return ValueTask.FromResult(0);
            for (int i = 0; i < memory.Length; i++) memory.Span[i] = (byte)offset++;
            return ValueTask.FromResult(memory.Length);
        }, capacity, deadline.Token).AsTask();
        try
        {
            ReadResult head = await channel.Reverse.ReadAsync(capacity - 1, token: deadline.Token);
            ReadResult boundary = await channel.Reverse.ReadAsync(2, token: deadline.Token);
            ReadResult tail = await channel.Reverse.ReadAsync(capacity - 1, token: deadline.Token);
            Assert.That(await pumping, Is.EqualTo(IOResult.Ok));
            await channel.CloseAsync();
            Assert.That(head.Data.ToArray(), Is.EqualTo(Enumerable.Range(0, 7).Select(i => (byte)i)));
            Assert.That(boundary.Data.ToArray(), Is.EqualTo(new byte[] { 7, 8 }));
            Assert.That(tail.Data.ToArray(), Is.EqualTo(Enumerable.Range(9, 7).Select(i => (byte)i)));

            HashSet<byte[]> arrays = new(ReferenceEqualityComparer.Instance);
            foreach (ReadOnlyMemory<byte> memory in boundary.Data)
            {
                Assert.That(MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment), Is.True);
                arrays.Add(segment.Array!);
            }
            Assert.That(arrays, Has.Count.EqualTo(2),
                "A read beginning at the end of a block can retain two partially used boundary blocks.");
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [Test]
    public async Task SourceWaitsForTheWholePublishedChunkBeforeReceivingAgain()
    {
        Channel channel = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        int calls = 0;
        Task<IOResult> pumping = ((IWriter)channel).WriteFromAsync((memory, _) =>
        {
            if (++calls > 1) return ValueTask.FromResult(0);
            new byte[] { 3, 7, 11, 19 }.CopyTo(memory);
            return ValueTask.FromResult(4);
        }, 8, deadline.Token).AsTask();
        try
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pumping.IsCompleted, Is.False);
            ReadResult head = await channel.Reverse.ReadAsync(2, token: deadline.Token);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pumping.IsCompleted, Is.False);
            ReadResult tail = await channel.Reverse.ReadAsync(2, token: deadline.Token);
            Assert.That(await pumping, Is.EqualTo(IOResult.Ok));
            Assert.That(head.Data.ToArray(), Is.EqualTo(new byte[] { 3, 7 }));
            Assert.That(tail.Data.ToArray(), Is.EqualTo(new byte[] { 11, 19 }));
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [TestCase(IOResult.Aborted)]
    [TestCase(IOResult.Cancelled)]
    [TestCase(IOResult.Ended)]
    public async Task RejectedWriteStopsReceiving(IOResult rejection)
    {
        IWriter writer = new RejectingWriter(rejection);
        int calls = 0;
        IOResult result = await writer.WriteFromAsync((memory, _) =>
        {
            calls++;
            memory.Span[0] = 42;
            return ValueTask.FromResult(1);
        }, 8);
        Assert.That(result, Is.EqualTo(rejection));
        Assert.That(calls, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SourceFailurePreservesAlreadyDeliveredBytes(bool cancel)
    {
        Channel channel = new();
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IOException failure = new("Receive failed");
        int calls = 0;
        Task<IOResult> pumping = ((IWriter)channel).WriteFromAsync(async (memory, token) =>
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
        }, 8, cancellation.Token).AsTask();
        try
        {
            ReadResult read = await channel.Reverse.ReadAsync(1, token: deadline.Token);
            await receiving.Task.WaitAsync(deadline.Token);
            if (cancel) cancellation.Cancel();
            else resume.TrySetResult();
            if (cancel) Assert.CatchAsync<OperationCanceledException>(async () => await pumping.WaitAsync(deadline.Token));
            else Assert.That(Assert.ThrowsAsync<IOException>(async () => await pumping.WaitAsync(deadline.Token)), Is.SameAs(failure));
            await channel.AbortAsync();
            Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 42 }));
        }
        finally
        {
            cancellation.Cancel();
            await channel.CloseAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PendingWriteStopsOnCancellationOrClose(bool cancel)
    {
        Channel channel = new();
        using CancellationTokenSource cancellation = new();
        int calls = 0;
        Task<IOResult> pumping = ((IWriter)channel).WriteFromAsync((memory, _) =>
        {
            calls++;
            memory.Span[0] = 42;
            return ValueTask.FromResult(1);
        }, 8, cancellation.Token).AsTask();
        Assert.That(pumping.IsCompleted, Is.False);
        if (cancel) cancellation.Cancel();
        else await channel.AbortAsync();
        try
        {
            Assert.That(await pumping.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(cancel ? IOResult.Cancelled : IOResult.Aborted));
            Assert.That(calls, Is.EqualTo(1));
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [TestCase(-1)]
    [TestCase(9)]
    public void InvalidReceiveCountIsRejected(int length)
    {
        IWriter writer = new RejectingWriter(IOResult.Ok);
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.WriteFromAsync((_, _) => ValueTask.FromResult(length), 8));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public void InvalidCapacityIsRejectedBeforeReceiving(int capacity)
    {
        IWriter writer = new RejectingWriter(IOResult.Ok);
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await writer.WriteFromAsync((_, _) => throw new AssertionException("The source must not be called."), capacity));
    }

    [Test]
    public void AlreadyCancelledPumpDoesNotReceive()
    {
        IWriter writer = new RejectingWriter(IOResult.Ok);
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await writer.WriteFromAsync((_, _) => throw new AssertionException("The source must not be called."), 8, new CancellationToken(true)));
    }

    private sealed class RejectingWriter(IOResult result) : IWriter
    {
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => ValueTask.FromResult(result);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
            => throw new AssertionException("The pump must not write EOF.");
    }
}
