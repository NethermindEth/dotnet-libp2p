// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core.Tests;

public class ReaderWriterTests
{
    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested()
    {
        Channel.ReaderWriter readerWriter = new();
        bool isWritten = false;
        Task wrote = Task.Run(async () =>
        {
            await WriteBytesAsync(readerWriter, new byte[] { 1, 2, 3, 4 });
            isWritten = true;
        });

        await Task.Delay(100);
        Assert.That(isWritten, Is.False);

        using (ReadResult chunk1 = await readerWriter.ReadAsync(1).OrThrow())
        {
            Assert.That(chunk1.Data.ToArray(), Is.EquivalentTo(new byte[] { 1 }));
            Assert.That(isWritten, Is.False);
        }

        using (ReadResult chunk2 = await readerWriter.ReadAsync(2).OrThrow())
        {
            Assert.That(chunk2.Data.ToArray(), Is.EquivalentTo(new byte[] { 2, 3 }));
            Assert.That(isWritten, Is.False);
        }

        using (ReadResult chunk3 = await readerWriter.ReadAsync(1).OrThrow())
        {
            Assert.That(chunk3.Data.ToArray(), Is.EquivalentTo(new byte[] { 4 }));
        }

        await wrote;
        Assert.That(isWritten, Is.True);
    }

    [Test]
    public async Task Test_ChannelReads_MultipleWrites()
    {
        Channel.ReaderWriter readerWriter = new();
        _ = Task.Run(async () =>
        {
            await WriteBytesAsync(readerWriter, new byte[] { 1 });
            await WriteBytesAsync(readerWriter, new byte[] { 2 });
        });

        using ReadResult allTheData = await readerWriter.ReadAsync(2).OrThrow();
        Assert.That(allTheData.Data.ToArray(), Is.EquivalentTo(new byte[] { 1, 2 }));
    }

    [Test]
    public async Task Test_ChannelReads_SequentialChunks()
    {
        Channel.ReaderWriter readerWriter = new();
        ValueTask<ReadResult> t1 = readerWriter.ReadAsync(2).OrThrow();
        ValueTask<ReadResult> t2 = readerWriter.ReadAsync(2).OrThrow();

        await WriteBytesAsync(readerWriter, new byte[] { 1 });
        await WriteBytesAsync(readerWriter, new byte[] { 2 });
        await WriteBytesAsync(readerWriter, new byte[] { 3 });
        await WriteBytesAsync(readerWriter, new byte[] { 4 });

        using ReadResult chunk1 = await t1;
        using ReadResult chunk2 = await t2;
        Assert.That(chunk1.Data.ToArray(), Is.EquivalentTo(new byte[] { 1, 2 }));
        Assert.That(chunk2.Data.ToArray(), Is.EquivalentTo(new byte[] { 3, 4 }));
    }

    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested2()
    {
        Channel.ReaderWriter readerWriter = new();
        _ = Task.Run(async () => await WriteBytesAsync(readerWriter, new byte[] { 1, 2 }));

        using ReadResult res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAny).OrThrow();
        Assert.That(res1.Length, Is.EqualTo(2));
    }

    [Test]
    public async Task Test_ChannelReadsNothing_WhenItIsDoNotWaitAndEmpty()
    {
        Channel.ReaderWriter readerWriter = new();

        using (ReadResult anyData = await readerWriter.ReadAsync(0, ReadBlockingMode.DoNotWait).OrThrow())
        {
            Assert.That(anyData.Length, Is.EqualTo(0));
        }

        using (ReadResult anyData = await readerWriter.ReadAsync(1, ReadBlockingMode.DoNotWait).OrThrow())
        {
            Assert.That(anyData.Length, Is.EqualTo(0));
        }

        using (ReadResult anyData = await readerWriter.ReadAsync(10, ReadBlockingMode.DoNotWait).OrThrow())
        {
            Assert.That(anyData.Length, Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Test_ChannelWrites_Eof()
    {
        Channel.ReaderWriter readerWriter = new();

        _ = Task.Run(async () =>
        {
            await WriteBytesAsync(readerWriter, new byte[] { 1, 2, 3 });
            await readerWriter.WriteEofAsync();
        });

        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ok));
        using (ReadResult res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAll).OrThrow())
        {
            Assert.That(res1.Length, Is.EqualTo(3));
        }

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAny).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.DoNotWait).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAll).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }

    [TestCase(new byte[0])]
    [TestCase(new byte[] { 1, 2, 3 })]
    public async Task Test_ChannelWrites_CannotWriteAfterEof(byte[] toWrite)
    {
        Channel.ReaderWriter readerWriter = new();

        await readerWriter.WriteEofAsync();
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await WriteBytesAsync(readerWriter, toWrite).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Test_ChannelWrites_CannotReadAll_OnePacket()
    {
        Channel.ReaderWriter readerWriter = new();

        _ = Task.Run(async () =>
        {
            await WriteBytesAsync(readerWriter, new byte[] { 1, 2, 3 });
            await readerWriter.WriteEofAsync();
        });

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(5, ReadBlockingMode.WaitAll).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Test_ChannelWrites_CannotReadAll_Fragmented()
    {
        Channel.ReaderWriter readerWriter = new();

        _ = Task.Run(async () =>
        {
            await WriteBytesAsync(readerWriter, new byte[] { 1 });
            await WriteBytesAsync(readerWriter, new byte[] { 2, 3 });
            await WriteBytesAsync(readerWriter, new byte[] { 4 });
            await readerWriter.WriteEofAsync();
        });

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(5, ReadBlockingMode.WaitAll).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task PooledBuffer_full_read_drops_channel_link()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buffer = PooledBuffer.Rent(5);
        "hello"u8.CopyTo(buffer.Span);

        Task<IOResult> writeTask = channel.WriteAsync(buffer, buffer.Length).AsTask();
        await Task.Yield();
        Assert.That(buffer.RefCount, Is.EqualTo(2));

        using (ReadResult read = await remote.ReadAsync(buffer.Length, ReadBlockingMode.WaitAll))
        {
            Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(read.Data.ToArray(), Is.EquivalentTo("hello"u8.ToArray()));
            Assert.That(await writeTask, Is.EqualTo(IOResult.Ok));
            Assert.That(buffer.RefCount, Is.EqualTo(2));
        }

        buffer.Dispose();
        Assert.That(buffer.RefCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Multi_buffer_write_is_atomic_and_ordered()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buf1 = PooledBuffer.Rent(3);
        PooledBuffer buf2 = PooledBuffer.Rent(2);
        PooledBuffer buf3 = PooledBuffer.Rent(4);
        "abc"u8.CopyTo(buf1.Span);
        "de"u8.CopyTo(buf2.Span);
        "fghi"u8.CopyTo(buf3.Span);

        using PooledBuffer.Slice slice1 = buf1[0..3];
        using PooledBuffer.Slice slice2 = buf2[0..2];
        using PooledBuffer.Slice slice3 = buf3[0..4];
        PooledBuffer.Slice[] slices = [slice1, slice2, slice3];

        Task<ReadResult> readTask = remote.ReadAsync(9, ReadBlockingMode.WaitAll).AsTask();
        Assert.That(await channel.WriteAsync(slices), Is.EqualTo(IOResult.Ok));

        using ReadResult result = await readTask;
        Assert.That(result.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(result.Data.ToArray(), Is.EquivalentTo("abcdefghi"u8.ToArray()));

        buf1.Dispose();
        buf2.Dispose();
        buf3.Dispose();
    }

    [Test]
    public void PooledBuffer_slice_can_prepend_reserved_headroom()
    {
        using PooledBuffer.Slice payload = PooledBuffer.RentSlice(3, headroom: 2);
        "abc"u8.CopyTo(payload.Span);

        Assert.That(payload.PrependableLength, Is.EqualTo(2));
        Assert.That(payload.TryPrepend(2, out PooledBuffer.Slice frame), Is.True);

        using (frame)
        {
            "xy"u8.CopyTo(frame.Span[..2]);
            Assert.That(frame.ReadOnlySpan.ToArray(), Is.EquivalentTo("xyabc"u8.ToArray()));
        }

        Assert.That(payload.TryPrepend(3, out _), Is.False);
    }

    [Test]
    public void PooledBuffer_subslice_does_not_prepend_into_payload()
    {
        using PooledBuffer.Slice payload = PooledBuffer.RentSlice(4, headroom: 2);
        "abcd"u8.CopyTo(payload.Span);

        using PooledBuffer.Slice first = payload.SliceRange(0, 2);
        Assert.That(first.TryPrepend(2, out PooledBuffer.Slice frame), Is.True);
        frame.Dispose();

        using PooledBuffer.Slice tail = payload.SliceRange(1, 3);
        Assert.That(tail.PrependableLength, Is.Zero);
        Assert.That(tail.TryPrepend(1, out _), Is.False);
    }

    [Test]
    public async Task Channel_preserves_prepend_headroom_for_zero_copy_reads()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        using PooledBuffer.Slice payload = PooledBuffer.RentSlice(3, headroom: 2);
        "abc"u8.CopyTo(payload.Span);

        Task<ReadResult> readTask = remote.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask();
        Assert.That(await channel.WriteAsync(payload), Is.EqualTo(IOResult.Ok));

        using ReadResult read = await readTask;
        using PooledBuffer.Slice received = read.ToSlice();

        Assert.That(received.TryPrepend(2, out PooledBuffer.Slice frame), Is.True);
        using (frame)
        {
            "xy"u8.CopyTo(frame.Span[..2]);
            Assert.That(frame.ReadOnlySpan.ToArray(), Is.EquivalentTo("xyabc"u8.ToArray()));
        }
    }

    [Test]
    public void ReadResult_default_slice_does_not_invent_prepend_headroom()
    {
        PooledBuffer buffer = PooledBuffer.Rent(5);
        "xabcd"u8.CopyTo(buffer.Span);

        using ReadResult read = ReadResult.Ok(buffer, 1, 4);
        using PooledBuffer.Slice slice = read.ToSlice();

        Assert.That(slice.PrependableLength, Is.Zero);
        Assert.That(slice.TryPrepend(1, out _), Is.False);
    }

    private static async ValueTask<IOResult> WriteBytesAsync(IWriter writer, byte[] bytes)
    {
        using PooledBuffer buffer = PooledBuffer.Rent(bytes.Length);
        bytes.CopyTo(buffer.Span);
        return await writer.WriteAsync(buffer, bytes.Length);
    }
}
