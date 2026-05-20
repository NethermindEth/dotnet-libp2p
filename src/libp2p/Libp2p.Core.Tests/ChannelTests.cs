// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Extensions;

namespace Nethermind.Libp2p.Core.Tests;

public class ChannelTests
{
    [Test]
    public async Task Test_AsStream_ReadsDataWrittenToReverseChannel()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        byte[] data = [1, 2, 3, 4];

        Task write = stream.WriteAsync(data, 0, data.Length);

        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer, 0, buffer.Length);
        await write;

        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(buffer, Is.EqualTo(data));
    }

    [Test]
    public async Task Test_AsStream_ReadsDataWrittenWithMemoryOverloads()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using Stream reverseStream = channel.Reverse.AsStream();

        byte[] data = [5, 6, 7, 8];

        ValueTask write = stream.WriteAsync(data.AsMemory());

        byte[] buffer = new byte[4];
        int bytesRead = await reverseStream.ReadAsync(buffer.AsMemory());
        await write;

        Assert.That(bytesRead, Is.EqualTo(data.Length));
        Assert.That(buffer, Is.EqualTo(data));
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthReadReturnsImmediately()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        byte[] buffer = new byte[4];
        int bytesRead = await stream.ReadAsync(buffer, 0, 0);

        Assert.That(bytesRead, Is.Zero);
        Assert.That(buffer, Is.EqualTo(new byte[4]));
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthReadReturnsAfterEnd()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        await channel.CloseAsync();
        byte[] buffer = new byte[4];
        int endedRead = await stream.ReadAsync(buffer, 0, 1);
        int emptyRead = await stream.ReadAsync(buffer, 0, 0).WaitAsync(TimeSpan.FromSeconds(1));
        int emptyMemoryRead = await stream.ReadAsync(Memory<byte>.Empty).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(endedRead, Is.Zero);
            Assert.That(emptyRead, Is.Zero);
            Assert.That(emptyMemoryRead, Is.Zero);
            Assert.That(stream.CanRead, Is.False);
        });
    }

    [Test]
    public void Test_AsStream_ZeroLengthReadValidatesOffset()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        byte[] buffer = new byte[1];

        Assert.Throws<ArgumentOutOfRangeException>(() => _ = stream.ReadAsync(buffer, 2, 0, CancellationToken.None));
    }

    [Test]
    public async Task Test_AsStream_ZeroLengthWriteCompletes()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();

        await stream.WriteAsync(Array.Empty<byte>(), 0, 0);

        Assert.That(stream.CanWrite, Is.True);
    }

    [Test]
    public void Test_AsStream_ReadAsyncCancellationThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

#pragma warning disable CA2022 // Intentionally validate Stream.ReadAsync cancellation behavior.
        Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1], 0, 1, cts.Token));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.ReadAsync(new byte[1].AsMemory(), cts.Token));
#pragma warning restore CA2022
        Assert.That(stream.CanRead, Is.True);
    }

    [Test]
    public void Test_AsStream_WriteAsyncCancellationThrows()
    {
        Channel channel = new();
        using Stream stream = channel.AsStream();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.WriteAsync([1], 0, 1, cts.Token));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.WriteAsync(new byte[] { 1 }.AsMemory(), cts.Token));
        Assert.That(stream.CanWrite, Is.True);
    }

    [Test]
    public void Test_AsStream_NullChannelThrows()
    {
        IChannel? channel = null;

        Assert.Throws<ArgumentNullException>(() => channel!.AsStream());
    }
}
