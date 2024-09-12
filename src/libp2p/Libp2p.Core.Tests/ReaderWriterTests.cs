// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Exceptions;
using System.Buffers;

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
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3, 4]));
            isWritten = true;
        });
        await Task.Delay(100);
        Assert.That(isWritten, Is.False);
        ReadOnlySequence<byte> chunk1 = await readerWriter.ReadAsync(1).OrThrow();
        Assert.Multiple(() =>
        {
            Assert.That(chunk1.ToArray(), Is.EquivalentTo(new byte[] { 1 }));
            Assert.That(isWritten, Is.False);
        });
        ReadOnlySequence<byte> chunk2 = await readerWriter.ReadAsync(2).OrThrow();
        Assert.Multiple(() =>
        {
            Assert.That(chunk2.ToArray(), Is.EquivalentTo(new byte[] { 2, 3 }));
            Assert.That(isWritten, Is.False);
        });
        ReadOnlySequence<byte> chunk3 = await readerWriter.ReadAsync(1).OrThrow();
        Assert.That(chunk3.ToArray(), Is.EquivalentTo(new byte[] { 4 }));
        await wrote;
        Assert.That(isWritten, Is.True);
    }

    [Test]
    public async Task Test_ChannelReads_MultipleWrites()
    {
        Channel.ReaderWriter readerWriter = new();
        _ = Task.Run(async () =>
        {
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1]));
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([2]));
        });
        ReadOnlySequence<byte> allTheData = await readerWriter.ReadAsync(2).OrThrow();
        Assert.That(allTheData.ToArray(), Is.EquivalentTo(new byte[] { 1, 2 }));
    }

    [Test]
    public async Task Test_ChannelReads_SequentialChunks()
    {
        Channel.ReaderWriter readerWriter = new();
        ValueTask<ReadOnlySequence<byte>> t1 = readerWriter.ReadAsync(2).OrThrow();
        ValueTask<ReadOnlySequence<byte>> t2 = readerWriter.ReadAsync(2).OrThrow();
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1]));
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>([2]));
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>([3]));
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>([4]));
        ReadOnlySequence<byte> chunk1 = await t1;
        ReadOnlySequence<byte> chunk2 = await t2;
        Assert.That(chunk1.ToArray(), Is.EquivalentTo(new byte[] { 1, 2 }));
        Assert.That(chunk2.ToArray(), Is.EquivalentTo(new byte[] { 3, 4 }));
    }

    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested2()
    {
        Channel.ReaderWriter readerWriter = new();
        _ = Task.Run(async () => await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1, 2])));
        ReadOnlySequence<byte> res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAny).OrThrow();
        Assert.That(res1.ToArray().Length, Is.EqualTo(2));
    }

    [Test]
    public async Task Test_ChannelReadsNithing_WhenItIsDontWaitAndEmpty()
    {
        Channel.ReaderWriter readerWriter = new();
        ReadOnlySequence<byte> anyData = await readerWriter.ReadAsync(0, ReadBlockingMode.DontWait).OrThrow();
        Assert.That(anyData.ToArray(), Is.Empty);
        anyData = await readerWriter.ReadAsync(1, ReadBlockingMode.DontWait).OrThrow();
        Assert.That(anyData.ToArray(), Is.Empty);
        anyData = await readerWriter.ReadAsync(10, ReadBlockingMode.DontWait).OrThrow();
        Assert.That(anyData.ToArray(), Is.Empty);
    }

    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested3()
    {
        Channel.ReaderWriter readerWriter = new();
        ReadOnlySequence<byte> res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.DontWait).OrThrow();
        Assert.That(res1.ToArray().Length, Is.EqualTo(0));
    }

    [Test]
    public async Task Test_ChannelWrites_Eof()
    {
        Channel.ReaderWriter readerWriter = new();

        _ = Task.Run(async () =>
        {
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]));
            await readerWriter.WriteEofAsync();
        });

        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ok));
        ReadOnlySequence<byte> res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAll).OrThrow();

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAny).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.DontWait).OrThrow());
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

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.WriteAsync(new ReadOnlySequence<byte>(toWrite)).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Test_ChannelWrites_CanReadAny()
    {
        Channel.ReaderWriter readerWriter = new();

        _ = Task.Run(async () =>
        {
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]));
            await readerWriter.WriteEofAsync();
        });

        ReadOnlySequence<byte> res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAll).OrThrow();

        Assert.That(res1, Has.Length.EqualTo(3));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAny).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.DontWait).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAll).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task Test_ChannelWrites_CannotReadAll_OnePacket()
    {
        Channel.ReaderWriter readerWriter = new();

        _ = Task.Run(async () =>
        {
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1, 2, 3]));
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
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([1]));
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([2, 3]));
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>([4]));
            await readerWriter.WriteEofAsync();
        });

        Assert.ThrowsAsync<ChannelClosedException>(async () => await readerWriter.ReadAsync(5, ReadBlockingMode.WaitAll).OrThrow());
        Assert.That(await readerWriter.CanReadAsync(), Is.EqualTo(IOResult.Ended));
    }
}
