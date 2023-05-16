// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4 }));
            isWritten = true;
        });
        await Task.Delay(100);
        Assert.That(isWritten, Is.False);
        ReadOnlySequence<byte> chunk1 = await readerWriter.ReadAsync(1);
        Assert.That(chunk1.ToArray(), Is.EquivalentTo(new byte[] { 1 }));
        Assert.That(isWritten, Is.False);
        ReadOnlySequence<byte> chunk2 = await readerWriter.ReadAsync(2);
        Assert.That(chunk2.ToArray(), Is.EquivalentTo(new byte[] { 2, 3 }));
        Assert.That(isWritten, Is.False);
        ReadOnlySequence<byte> chunk3 = await readerWriter.ReadAsync(1);
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
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }));
            await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 }));
        });
        ReadOnlySequence<byte> allTheData = await readerWriter.ReadAsync(2);
        Assert.That(allTheData.ToArray(), Is.EquivalentTo(new byte[] { 1, 2 }));
    }

    [Test]
    public async Task Test_ChannelReads_SequentialChunks()
    {
        Channel.ReaderWriter readerWriter = new();
        ValueTask<ReadOnlySequence<byte>> t1 = readerWriter.ReadAsync(2);
        ValueTask<ReadOnlySequence<byte>> t2 = readerWriter.ReadAsync(2);
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }));
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 }));
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 3 }));
        await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 4 }));
        ReadOnlySequence<byte> chunk1 = await t1;
        ReadOnlySequence<byte> chunk2 = await t2;
        Assert.That(chunk1.ToArray(), Is.EquivalentTo(new byte[] { 1, 2 }));
        Assert.That(chunk2.ToArray(), Is.EquivalentTo(new byte[] { 3, 4 }));
    }

    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested2()
    {
        Channel.ReaderWriter readerWriter = new();
        _ = Task.Run(async () => await readerWriter.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 })));
        ReadOnlySequence<byte> res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.WaitAny);
        Assert.That(res1.ToArray().Length, Is.EqualTo(2));
    }

    [Test]
    public async Task Test_ChannelReadsNithing_WhenItIsDontWaitAndEmpty()
    {
        Channel.ReaderWriter readerWriter = new();
        ReadOnlySequence<byte> anyData = await readerWriter.ReadAsync(0, ReadBlockingMode.DontWait);
        Assert.That(anyData.ToArray(), Is.Empty);
        anyData = await readerWriter.ReadAsync(1, ReadBlockingMode.DontWait);
        Assert.That(anyData.ToArray(), Is.Empty);
        anyData = await readerWriter.ReadAsync(10, ReadBlockingMode.DontWait);
        Assert.That(anyData.ToArray(), Is.Empty);
    }

    [Test]
    public async Task Test_ChannelWrites_WhenReadIsRequested3()
    {
        Channel.ReaderWriter readerWriter = new();
        ReadOnlySequence<byte> res1 = await readerWriter.ReadAsync(3, ReadBlockingMode.DontWait);
        Assert.That(res1.ToArray().Length, Is.EqualTo(0));
    }
}
