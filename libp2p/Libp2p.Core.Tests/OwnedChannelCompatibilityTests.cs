// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using Nethermind.Channels;
using Nethermind.Libp2p.Core.Metrics;
using NSubstitute;

namespace Nethermind.Libp2p.Core.Tests;

public class OwnedChannelCompatibilityTests
{
    [TestCase(false)]
    [TestCase(true)]
    [NonParallelizable]
    public async Task ThrowingMetricCallbackReleasesTheUnreturnedRead(bool owned)
    {
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument == Libp2pMetrics.DataReceivedBytes) meterListener.EnableMeasurementEvents(instrument);
        };
        InvalidOperationException failure = new("Metric callback failed");
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw failure);
        listener.Start();
        PoisonPool pool = new();
        Channel channel = new();
        PooledBuffer source = PooledBuffer.Rent(1, pool);
        Task<IOResult> write = channel.WriteAsync(source, 1).AsTask();
        source.Dispose();
        try
        {
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                using ReadResult read = owned ? await channel.Reverse.ReadOwnedAsync(1) : await channel.Reverse.ReadAsync(1);
            }), Is.SameAs(failure));
            Assert.That(await write, Is.EqualTo(IOResult.Ok));
            Assert.That(pool.Returns, Is.EqualTo(1));
        }
        finally
        {
            await channel.CloseAsync();
        }
    }

    [Test]
    public async Task AsyncUnwrapDetachesAndDisposesOwnedReads()
    {
        PoisonPool pool = new();
        Channel channel = new();
        PooledBuffer source = PooledBuffer.Rent(3, pool);
        new byte[] { 3, 7, 11 }.CopyTo(source.Memory);
        Task<IOResult> write = channel.WriteAsync(source, 3).AsTask();
        source.Dispose();
        ReadOnlySequence<byte> data = await channel.Reverse.ReadOwnedAsync(3).OrThrow();
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        Assert.That(pool.Returns, Is.EqualTo(1));
        Assert.That(data.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11 }));
        await channel.CloseAsync();
    }

    [Test]
    public async Task LegacyReadDetachesPooledStorageBeforeReturning()
    {
        PoisonPool pool = new();
        Channel channel = new();
        PooledBuffer source = PooledBuffer.Rent(4, pool);
        new byte[] { 3, 7, 11, 19 }.CopyTo(source.Memory);
        Task<IOResult> write = channel.WriteAsync(source, 4).AsTask();
        ReadResult read = await channel.Reverse.ReadAsync(4);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        source.Dispose();
        Assert.That(pool.Returns, Is.EqualTo(1));
        Assert.That(read.IsOwned, Is.False);
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11, 19 }));
        read.Dispose();
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11, 19 }));
        await channel.CloseAsync();
    }

    [Test]
    public async Task OwnedReadKeepsPoolRentalUntilTheConsumerDisposesIt()
    {
        PoisonPool pool = new();
        Channel channel = new();
        PooledBuffer source = PooledBuffer.Rent(4, pool);
        new byte[] { 3, 7, 11, 19 }.CopyTo(source.Memory);
        Task<IOResult> write = channel.WriteAsync(source, 4).AsTask();
        using ReadResult read = await channel.Reverse.ReadOwnedAsync(4);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        source.Dispose();
        await channel.AbortAsync();
        Assert.That(pool.Returns, Is.Zero);
        Assert.That(read.IsOwned, Is.True);
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11, 19 }));
        read.Dispose();
        Assert.That(pool.Returns, Is.EqualTo(1));
        Assert.Throws<ObjectDisposedException>(() => _ = read.Data);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BorrowedWritesRemainCopyFreeForBothReadApis(bool owned)
    {
        Channel channel = new();
        byte[] data = [3, 7, 11];
        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(data)).AsTask();
        using ReadResult read = owned ? await channel.Reverse.ReadOwnedAsync(3) : await channel.Reverse.ReadAsync(3);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        Assert.That(read.IsOwned, Is.False);
        Assert.That(MemoryMarshal.TryGetArray(read.Data.First, out ArraySegment<byte> segment), Is.True);
        Assert.That(segment.Array, Is.SameAs(data));
        await channel.CloseAsync();
    }

    [Test]
    public async Task WriterWithoutOwnershipSupportDetachesPooledInput()
    {
        Channel channel = new();
        IWriter writer = new LegacyWriter(channel);
        PoisonPool pool = new();
        PooledBuffer source = PooledBuffer.Rent(3, pool);
        new byte[] { 3, 7, 11 }.CopyTo(source.Memory);
        Task<IOResult> write = writer.WriteAsync(source, 3).AsTask();
        source.Dispose();
        ReadResult read = await channel.Reverse.ReadAsync(3);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        Assert.That(pool.Returns, Is.EqualTo(1));
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11 }));
        await channel.CloseAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ForwardingKeepsBytesAliveAfterTheReadIsDisposed(bool legacyDestination)
    {
        Channel source = new();
        Channel destination = new();
        IWriter writer = legacyDestination ? new LegacyWriter(destination) : destination;
        PoisonPool pool = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try
        {
            using PooledBuffer buffer = PooledBuffer.Rent(5, pool);
            new byte[] { 0, 3, 7, 11, 0 }.CopyTo(buffer.Memory);
            Task<IOResult> write = source.WriteAsync(buffer, 3, offset: 1, token: timeout.Token).AsTask();
            buffer.Dispose();
            using ReadResult read = await source.Reverse.ReadOwnedAsync(3, token: timeout.Token);
            Assert.That(await write, Is.EqualTo(IOResult.Ok));

            ValueTask<IOResult> forwarding = read.WriteToAsync(writer, timeout.Token);
            read.Dispose();
            Assert.That(pool.Returns, Is.EqualTo(legacyDestination ? 1 : 0));
            using ReadResult received = await destination.Reverse.ReadOwnedAsync(3, token: timeout.Token);
            Assert.That(await forwarding, Is.EqualTo(IOResult.Ok));
            Assert.That(received.IsOwned, Is.EqualTo(!legacyDestination));
            Assert.That(received.Data.ToArray(), Is.EqualTo(new byte[] { 3, 7, 11 }));
            Assert.That(pool.Returns, Is.EqualTo(legacyDestination ? 1 : 0));
            received.Dispose();
            Assert.That(pool.Returns, Is.EqualTo(1));
        }
        finally
        {
            await source.CloseAsync();
            await destination.CloseAsync();
        }
    }

    [Test]
    public void CustomReaderTransfersTheSuppliedSliceToItsConsumer()
    {
        PoisonPool pool = new();
        using PooledBuffer buffer = PooledBuffer.Rent(4, pool);
        new byte[] { 0, 3, 7, 0 }.CopyTo(buffer.Memory);
        using ReadResult read = ReadResult.FromOwnedSlice(buffer[1..3]);
        buffer.Dispose();
        Assert.That(read.IsOwned, Is.True);
        Assert.That(read.OrThrow().ToArray(), Is.EqualTo(new byte[] { 3, 7 }));
        read.Dispose();
        Assert.That(pool.Returns, Is.EqualTo(1));
    }

    [Test]
    public async Task ForwardingBorrowedDataPreservesSegmentsAndPassesTheWriteResultAndToken()
    {
        ReadOnlySequence<byte> sequence = new ReadOnlySequence<byte>(new byte[] { 1, 2 }).Append(new byte[] { 3, 4 });
        using CancellationTokenSource cancellation = new();
        IWriter writer = Substitute.For<IWriter>();
        writer.WriteAsync(sequence, cancellation.Token).Returns(ValueTask.FromResult(IOResult.Aborted));

        ReadResult read = new() { Data = sequence };
        Assert.That(await read.WriteToAsync(writer, cancellation.Token), Is.EqualTo(IOResult.Aborted));
        await writer.Received(1).WriteAsync(sequence, cancellation.Token);
    }

    [TestCase(IOResult.Ended)]
    [TestCase(IOResult.Cancelled)]
    [TestCase(IOResult.Aborted)]
    [TestCase(IOResult.InternalError)]
    public async Task ForwardingAnUnsuccessfulReadLeavesTheDestinationAlone(IOResult status)
    {
        IWriter writer = Substitute.For<IWriter>();
        ReadResult read = new() { Result = status };
        Assert.That(await read.WriteToAsync(writer), Is.EqualTo(status));
        Assert.That(writer.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public async Task StreamReadReleasesItsPooledReadAfterCopyingToTheDestination()
    {
        Channel channel = new();
        using Stream stream = new ChannelStream(channel.Reverse);
        PoisonPool pool = new();
        PooledBuffer source = PooledBuffer.Rent(3, pool);
        new byte[] { 3, 7, 11 }.CopyTo(source.Memory);
        Task<IOResult> write = channel.WriteAsync(source, 3).AsTask();
        source.Dispose();
        byte[] destination = new byte[3];
        await stream.ReadExactlyAsync(destination);
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        Assert.That(pool.Returns, Is.EqualTo(1));
        Assert.That(destination, Is.EqualTo(new byte[] { 3, 7, 11 }));
    }

    [Test]
    public async Task EndingOwnedEnumerationReleasesItsCurrentRead()
    {
        Channel channel = new();
        PoisonPool pool = new();
        PooledBuffer source = PooledBuffer.Rent(1, pool);
        source.Span[0] = 42;
        Task<IOResult> write = channel.WriteAsync(source, 1).AsTask();
        source.Dispose();
        await foreach (ReadResult read in channel.Reverse.ReadAllOwnedAsync())
        {
            Assert.That(read.Data.FirstSpan[0], Is.EqualTo(42));
            Assert.That(pool.Returns, Is.Zero);
            break;
        }
        Assert.That(await write, Is.EqualTo(IOResult.Ok));
        Assert.That(pool.Returns, Is.EqualTo(1));
        await channel.CloseAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CancellingPendingEnumerationReportsTheCallerToken(bool owned)
    {
        Channel channel = new();
        using CancellationTokenSource cancellation = new();
        try
        {
            IReader reader = channel.Reader;
            Task reading = owned ? ConsumeAsync(reader.ReadAllOwnedAsync(cancellation.Token))
                : ConsumeAsync(reader.ReadAllAsync(cancellation.Token));
            Assert.That(reading.IsCompleted, Is.False);
            await cancellation.CancelAsync();

            OperationCanceledException? error = Assert.CatchAsync<OperationCanceledException>(
                async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(error!.CancellationToken, Is.EqualTo(cancellation.Token));
        }
        finally
        {
            await channel.CloseAsync();
        }

        static async Task ConsumeAsync<T>(IAsyncEnumerable<T> reads)
        {
            await foreach (T _ in reads) { }
        }
    }

    private sealed class LegacyWriter(Channel channel) : IWriter
    {
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
            => channel.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => channel.WriteEofAsync(token);
    }

    private sealed class PoisonPool : ArrayPool<byte>
    {
        public int Returns { get; private set; }
        public override byte[] Rent(int minimumLength) => new byte[minimumLength + 17];
        public override void Return(byte[] array, bool clearArray = false)
        {
            Returns++;
            array.AsSpan().Fill(0xee);
        }
    }
}
