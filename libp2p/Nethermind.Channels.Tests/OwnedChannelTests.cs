// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Reflection;
using NUnit.Framework;

namespace Nethermind.Channels.Tests;

public class OwnedChannelTests
{
    [Test]
    public async Task ConcurrentProtocolCompletionPreservesReplyEof()
    {
        Channel channel = new();
        Task<IOResult> request = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 })).AsTask();
        Task<IOResult> reply = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 3, 4 })).AsTask();
        using ReadResult read = await channel.Reverse.ReadAsync(2);
        Assert.That(await reply, Is.EqualTo(IOResult.Ok));

        Channel.ReaderWriter writer = (Channel.ReaderWriter)channel.Writer;
        Lock dataLock = (Lock)typeof(Channel.ReaderWriter)
            .GetField("_dataLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(writer)!;
        Exception? failure = null;
        Thread completing = new(() =>
        {
            try { channel.CompleteProtocolAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true };

        try
        {
            lock (dataLock)
            {
                completing.Start();
                // The only blocking operation on this dedicated thread is acquiring dataLock.
                // A competing completion must not mistake its EOF reservation for an active write.
                Assert.That(SpinWait.SpinUntil(
                    () => (completing.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5)), Is.True);
                channel.CompleteProtocolAsync().GetAwaiter().GetResult();
            }

            Assert.That(completing.Join(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(failure, Is.Null);
            Assert.That(await request.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Aborted));
            using ReadResult eof = await channel.Reverse.ReadAsync(1);
            Assert.That(eof.Result, Is.EqualTo(IOResult.Ended),
                "Discarding the unread request must preserve the completed reply's graceful EOF.");
            Assert.That(read.ToArray(), Is.EqualTo(new byte[] { 3, 4 }));
        }
        finally
        {
            completing.Join(TimeSpan.FromSeconds(5));
            await channel.AbortAsync();
        }
    }

    [Test]
    public async Task FragmentedWaitAllCoalescesAndReleasesInputLeases()
    {
        Channel channel = new();
        PoisonPool pool = new();
        byte[] expected = Enumerable.Range(0, 257).Select(i => (byte)(i % 251)).ToArray();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Task<ReadResult> reading = channel.Reverse.ReadAsync(expected.Length, token: deadline.Token).AsTask();
        try
        {
            foreach (byte value in expected)
            {
                using PooledBuffer input = PooledBuffer.Rent(4096, pool);
                input.Span[0] = value;
                Assert.That(await channel.WriteAsync(input, 1, token: deadline.Token), Is.EqualTo(IOResult.Ok));
            }

            using ReadResult result = await reading;
            Assert.That(result.IsOwned, Is.True);
            Assert.That(result.Sequence.IsSingleSegment, Is.True);
            Assert.That(result.ToArray(), Is.EqualTo(expected));
            Assert.That(pool.Returned, Is.EqualTo(expected.Length),
                "Coalescing must release consumed source buffers before acknowledging their writes.");
        }
        finally
        {
            await channel.AbortAsync();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IncompleteReadReleasesRetainedInputOnCancellationOrEof(bool cancel)
    {
        Channel channel = new();
        PoisonPool pool = new();
        using CancellationTokenSource cancellation = new();
        Task<ReadResult> reading = channel.Reverse.ReadAsync(1_000_000, token: cancellation.Token).AsTask();
        using (PooledBuffer input = PooledBuffer.Rent(4096, pool))
        {
            input.Span[0] = 42;
            Assert.That(await channel.WriteAsync(input, 1), Is.EqualTo(IOResult.Ok));
        }

        Assert.That(pool.Returned, Is.Zero);
        if (cancel) cancellation.Cancel();
        else Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        using ReadResult result = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result.Result, Is.EqualTo(cancel ? IOResult.Cancelled : IOResult.Ended));
        Assert.That(pool.Returned, Is.EqualTo(1));
        await channel.CloseAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnconsumedWriteReleasesChannelLeaseOnCancellationOrAbort(bool cancel)
    {
        Channel channel = new();
        PoisonPool pool = new();
        using CancellationTokenSource cancellation = new();
        Task<IOResult> writing;
        using (PooledBuffer input = PooledBuffer.Rent(8, pool))
        {
            input.Span.Fill(19);
            writing = channel.WriteAsync(input, 8, token: cancellation.Token).AsTask();
        }

        Assert.That(pool.Returned, Is.Zero);
        if (cancel) cancellation.Cancel();
        else await channel.AbortAsync();
        Assert.That(await writing.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(cancel ? IOResult.Cancelled : IOResult.Aborted));
        Assert.That(pool.Returned, Is.EqualTo(1));

        if (cancel)
        {
            byte[] later = [3, 7, 11];
            Task<IOResult> nextWrite = channel.WriteAsync(new ReadOnlySequence<byte>(later)).AsTask();
            using ReadResult read = await channel.Reverse.ReadAsync(3);
            Assert.That(await nextWrite, Is.EqualTo(IOResult.Ok));
            Assert.That(read.ToArray(), Is.EqualTo(later));
        }
        await channel.CloseAsync();
    }

    [Test]
    public async Task PartialReadKeepsCancelledWriteAndBothReadLeasesAlive()
    {
        Channel channel = new();
        PoisonPool pool = new();
        using CancellationTokenSource cancellation = new();
        Task<IOResult> writing;
        using (PooledBuffer input = PooledBuffer.Rent(8, pool))
        {
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(input.Memory);
            writing = channel.WriteAsync(input, 8, token: cancellation.Token).AsTask();
        }

        using ReadResult head = await channel.Reverse.ReadAsync(3);
        cancellation.Cancel();
        Assert.That(writing.IsCompleted, Is.False);
        using ReadResult tail = await channel.Reverse.ReadAsync(5);
        Assert.That(await writing.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(IOResult.Ok));
        await channel.CloseAsync();
        Assert.That(head.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(tail.ToArray(), Is.EqualTo(new byte[] { 4, 5, 6, 7, 8 }));
        Assert.That(pool.Returned, Is.Zero);
        head.Dispose();
        Assert.That(pool.Returned, Is.Zero);
        tail.Dispose();
        Assert.That(pool.Returned, Is.EqualTo(1));
    }

    [Test]
    public async Task HalfCloseDiscardsInboundOwnerAndPreservesOutboundReply()
    {
        Channel channel = new();
        PoisonPool inboundPool = new();
        PoisonPool outboundPool = new();
        Task<IOResult> inboundWrite;
        using (PooledBuffer incoming = PooledBuffer.Rent(8, inboundPool))
        {
            inboundWrite = channel.Reverse.WriteAsync(incoming, 8).AsTask();
        }
        await channel.CloseReadAsync();
        Assert.That(await inboundWrite.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(IOResult.Aborted));
        Assert.That(inboundPool.Returned, Is.EqualTo(1));

        Task<IOResult> reply;
        using (PooledBuffer outgoing = PooledBuffer.Rent(3, outboundPool))
        {
            new byte[] { 17, 23, 41 }.CopyTo(outgoing.Memory);
            reply = channel.WriteAsync(outgoing, 3).AsTask();
        }
        using ReadResult result = await channel.Reverse.ReadAsync(3);
        Assert.That(await reply.WaitAsync(TimeSpan.FromSeconds(10)), Is.EqualTo(IOResult.Ok));
        Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        using ReadResult eof = await channel.Reverse.ReadAsync(1);
        Assert.That(eof.Result, Is.EqualTo(IOResult.Ended));
        await channel;
        Assert.That(result.ToArray(), Is.EqualTo(new byte[] { 17, 23, 41 }));
        Assert.That(outboundPool.Returned, Is.Zero);
        result.Dispose();
        Assert.That(outboundPool.Returned, Is.EqualTo(1));
    }

    [Test]
    public async Task AbortCannotReturnStorageWhileReaderOwnsTheTransferredChunk()
    {
        Channel channel = new();
        using BlockingReturnPool firstPool = new();
        PoisonPool secondPool = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Task<ReadResult> reading = channel.Reverse.ReadAsync(2, token: deadline.Token).AsTask();
        using (PooledBuffer first = PooledBuffer.Rent(1, firstPool))
        {
            first.Span[0] = 11;
            Assert.That(await channel.WriteAsync(first, 1, token: deadline.Token), Is.EqualTo(IOResult.Ok));
        }

        Task<IOResult> writing;
        using (PooledBuffer second = PooledBuffer.Rent(1, secondPool))
        {
            second.Span[0] = 29;
            writing = channel.WriteAsync(second, 1, token: deadline.Token).AsTask();
        }

        try
        {
            await firstPool.Returning.Task.WaitAsync(deadline.Token);
            await channel.AbortAsync();
            Assert.That(await writing.WaitAsync(deadline.Token), Is.EqualTo(IOResult.Aborted));
            Assert.That(secondPool.Returned, Is.Zero,
                "The reader has taken this lease; teardown must not return its storage during accumulation.");
        }
        finally
        {
            firstPool.Resume.Set();
            await channel.AbortAsync();
        }

        using ReadResult result = await reading.WaitAsync(deadline.Token);
        Assert.That(result.Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(firstPool.Returned, Is.EqualTo(1));
        Assert.That(secondPool.Returned, Is.EqualTo(1));
    }

    [Test]
    public async Task BorrowedFragmentsKeepTheirBackingStorageAndSequenceShape()
    {
        Channel channel = new();
        byte[] first = [1, 2];
        byte[] second = [3, 4, 5];
        Task<ReadResult> reading = channel.Reverse.ReadAsync(5).AsTask();
        Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(first)), Is.EqualTo(IOResult.Ok));
        Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(second)), Is.EqualTo(IOResult.Ok));
        using ReadResult result = await reading;
        Assert.That(result.IsOwned, Is.False);
        Assert.That(result.Sequence.IsSingleSegment, Is.False);
        Assert.That(result.Sequence.First.Equals(first.AsMemory()), Is.True);
        Assert.That(result.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
        await channel.CloseAsync();
        Assert.That(result.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MixedOwnershipCoalescesWithoutChangingTheByteStream(bool ownedFirst)
    {
        Channel channel = new();
        PoisonPool pool = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        Task<ReadResult> reading = channel.Reverse.ReadAsync(4, token: deadline.Token).AsTask();
        for (int i = 0; i < 2; i++)
        {
            byte[] bytes = [(byte)(i * 2 + 1), (byte)(i * 2 + 2)];
            if ((i == 0) == ownedFirst)
            {
                using PooledBuffer input = PooledBuffer.Rent(2, pool);
                bytes.CopyTo(input.Memory);
                Assert.That(await channel.WriteAsync(input, 2, token: deadline.Token), Is.EqualTo(IOResult.Ok));
            }
            else
            {
                Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(bytes), deadline.Token), Is.EqualTo(IOResult.Ok));
            }
        }

        using ReadResult result = await reading;
        Assert.That(result.IsOwned, Is.True);
        Assert.That(result.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        Assert.That(pool.Returned, Is.EqualTo(1));
        await channel.CloseAsync();
    }

    private class PoisonPool : ArrayPool<byte>
    {
        private int _returned;
        public int Returned => Volatile.Read(ref _returned);
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false)
        {
            array.AsSpan().Fill(0xee);
            Interlocked.Increment(ref _returned);
        }
    }

    private sealed class BlockingReturnPool : PoisonPool, IDisposable
    {
        public TaskCompletionSource Returning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        public override void Return(byte[] array, bool clearArray = false)
        {
            Returning.TrySetResult();
            if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the pool return.");
            base.Return(array, clearArray);
        }
        public void Dispose() => Resume.Dispose();
    }
}
