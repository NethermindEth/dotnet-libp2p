// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Nethermind.Channels.Tests;

public class BasicTests
{
    [Test]
    public async Task SingleReadRetainsThePublishedStorageUntilDisposed()
    {
        Channel channel = new();
        PooledBuffer buffer = PooledBuffer.Rent(8);
        Encoding.UTF8.GetBytes("abcdefgh", buffer.Span);
        Memory<byte> published = buffer.Memory;
        Task<IOResult> write = channel.WriteAsync(buffer, buffer.Length).AsTask();
        using ReadResult result = await channel.Reverse.ReadAsync(8);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        buffer.Dispose();
        Assert.That(result.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(result.IsOwned, Is.True);
        Assert.That(result.Sequence.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("abcdefgh")));
        Assert.That(MemoryMarshal.TryGetArray(result.Memory, out ArraySegment<byte> readArray), Is.True);
        Assert.That(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)published, out ArraySegment<byte> sentArray), Is.True);
        Assert.That(readArray.Array, Is.SameAs(sentArray.Array));
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        result.Dispose();
        Assert.That(buffer.RefCount, Is.Zero);
        await channel.CloseAsync();
    }

    [Test]
    public async Task MultiBufferWritePreservesOrderAndCallerLeases()
    {
        Channel channel = new();
        using PooledBuffer first = PooledBuffer.Rent(3);
        using PooledBuffer second = PooledBuffer.Rent(2);
        Encoding.UTF8.GetBytes("abc", first.Span);
        Encoding.UTF8.GetBytes("de", second.Span);
        using PooledBuffer.Slice firstSlice = first[..];
        using PooledBuffer.Slice secondSlice = second[..];
        PooledBuffer.Slice[] slices = [firstSlice, secondSlice];
        Task<IOResult> write = channel.WriteAsync(slices).AsTask();
        using ReadResult result = await channel.Reverse.ReadAsync(5);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        Assert.That(result.Sequence.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("abcde")));
        Assert.That(firstSlice.ReadOnlyMemory.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("abc")));
        Assert.That(secondSlice.ReadOnlyMemory.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("de")));
        await channel.CloseAsync();
    }

    [Test]
    public async Task ConcurrentMultiBufferWritesRemainAtomic()
    {
        Channel channel = new();
        using PooledBuffer first = PooledBuffer.Rent(4);
        using PooledBuffer second = PooledBuffer.Rent(4);
        Encoding.UTF8.GetBytes("abcd", first.Span);
        Encoding.UTF8.GetBytes("WXYZ", second.Span);
        using PooledBuffer.Slice a = first[..2];
        using PooledBuffer.Slice b = first[2..];
        using PooledBuffer.Slice c = second[..1];
        using PooledBuffer.Slice d = second[1..];
        PooledBuffer.Slice[] firstParts = [a, b];
        PooledBuffer.Slice[] secondParts = [c, d];
        Task<IOResult> write1 = channel.WriteAsync(firstParts).AsTask();
        Task<IOResult> write2 = channel.WriteAsync(secondParts).AsTask();
        using ReadResult read1 = await channel.Reverse.ReadAsync(4);
        using ReadResult read2 = await channel.Reverse.ReadAsync(4);
        Assert.That(await write1, Is.EqualTo(IOResult.Ok));
        Assert.That(await write2, Is.EqualTo(IOResult.Ok));
        Assert.That(new[] { Encoding.UTF8.GetString(read1.Sequence.ToArray()), Encoding.UTF8.GetString(read2.Sequence.ToArray()) },
            Is.EquivalentTo(new[] { "abcd", "WXYZ" }));
        await channel.CloseAsync();
    }

    [Test]
    public async Task ForwardedSliceSurvivesOriginalOwnerAndReadDisposal()
    {
        Channel source = new();
        Channel destination = new();
        PooledBuffer buffer = PooledBuffer.Rent(4);
        Encoding.UTF8.GetBytes("wxyz", buffer.Span);
        Task<IOResult> write = source.WriteAsync(buffer, 4).AsTask();
        ReadResult read = await source.Reverse.ReadAsync(4);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        using PooledBuffer.Slice forwarding = read.ToSlice();
        buffer.Dispose();
        read.Dispose();
        Task<IOResult> forward = destination.WriteAsync(forwarding).AsTask();
        forwarding.Dispose();
        using ReadResult received = await destination.Reverse.ReadAsync(4);
        Assert.That(await forward, Is.EqualTo(IOResult.Ok));
        Assert.That(received.Sequence.ToArray(), Is.EqualTo(Encoding.UTF8.GetBytes("wxyz")));
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        received.Dispose();
        Assert.That(buffer.RefCount, Is.Zero);
        await source.CloseAsync();
        await destination.CloseAsync();
    }

    [Test]
    public async Task CopiedReadResultReleasesItsLeaseOnlyOnce()
    {
        Channel channel = new();
        using PooledBuffer buffer = PooledBuffer.Rent(1);
        Task<IOResult> write = channel.WriteAsync(buffer, 1).AsTask();
        ReadResult read = await channel.Reverse.ReadAsync(1);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        ReadResult alias = read;
        read.Dispose();
        alias.Dispose();
        Assert.That(buffer.RefCount, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => _ = alias.Sequence);
        await channel.CloseAsync();
    }
}
