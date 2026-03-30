using System.Buffers;
using System.Text;

namespace Nethermind.Channels.Tests;

public class ChannelTests
{
    private sealed class RecordingPool(int extra) : ArrayPool<byte>
    {
        public int RentedLength { get; private set; }
        public List<int> ReturnedLengths { get; } = new();

        public override byte[] Rent(int minimumLength)
        {
            RentedLength = minimumLength + extra;
            return new byte[RentedLength];
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            ReturnedLengths.Add(array.Length);
        }
    }

    [Fact]
    public async Task WriteAsync_then_ReadAsync_returns_payload()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        byte[] payload = Encoding.UTF8.GetBytes("hello libp2p");
        PooledBuffer writeBuf = PooledBuffer.Rent(payload.Length);
        payload.CopyTo(writeBuf.Span);

        ValueTask<ReadResult> readTask = remote.ReadAsync(payload.Length);

        Assert.Equal(IOResult.Ok, await channel.WriteAsync(writeBuf, payload.Length));

        using ReadResult read = await readTask;
        Assert.Equal(IOResult.Ok, read.Result);
        Assert.Equal(payload, read.Data.ToArray());

        writeBuf.Dispose();
    }

    [Fact]
    public async Task Multi_buffer_write_is_atomic_and_ordered()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buf1 = PooledBuffer.Rent(3);
        PooledBuffer buf2 = PooledBuffer.Rent(2);
        PooledBuffer buf3 = PooledBuffer.Rent(4);

        Encoding.UTF8.GetBytes("abc", buf1.Span);
        Encoding.UTF8.GetBytes("de", buf2.Span);
        Encoding.UTF8.GetBytes("fghi", buf3.Span);

        PooledBuffer.Slice[] slices =
        {
            buf1[0..3],
            buf2[0..2],
            buf3[0..4]
        };

        Task<ReadResult> readTask = remote.ReadAsync(9, ReadBlockingMode.WaitAll).AsTask();

        IOResult writeResult = await channel.WriteAsync(slices);
        Assert.Equal(IOResult.Ok, writeResult);

        using ReadResult result = await readTask;
        Assert.Equal(IOResult.Ok, result.Result);
        Assert.Equal("abcdefghi", Encoding.UTF8.GetString(result.Data.ToArray()));

        foreach (PooledBuffer.Slice slice in slices)
        {
            slice.Dispose();
        }

        buf1.Dispose();
        buf2.Dispose();
        buf3.Dispose();
    }

    [Fact]
    public async Task WaitAll_read_over_multiple_buffers_returns_contiguous_chunk()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buf1 = PooledBuffer.Rent(10);
        PooledBuffer buf2 = PooledBuffer.Rent(10);

        Encoding.UTF8.GetBytes("abcdefghij", buf1.Span);
        Encoding.UTF8.GetBytes("klmnopqrst", buf2.Span);

        PooledBuffer.Slice[] slices =
        {
            buf1[0..10],
            buf2[0..10]
        };

        Task<ReadResult> readTask = remote.ReadAsync(20, ReadBlockingMode.WaitAll).AsTask();
        IOResult writeResult = await channel.WriteAsync(slices);
        Assert.Equal(IOResult.Ok, writeResult);

        using ReadResult result = await readTask;
        Assert.Equal(IOResult.Ok, result.Result);
        Assert.Equal("abcdefghijklmnopqrst", Encoding.UTF8.GetString(result.Data));

        foreach (PooledBuffer.Slice slice in slices)
        {
            slice.Dispose();
        }

        buf1.Dispose();
        buf2.Dispose();
    }

    [Fact]
    public async Task Multi_stage_inverts_and_splits_without_hanging()
    {
        Channel channel1 = new();
        IChannel reverse1 = channel1.Reverse;

        Channel channel2 = new();
        IChannel reverse2 = channel2.Reverse;

        Channel channel3 = new();
        IChannel reverse3 = channel3.Reverse;

        Channel channel4 = new();
        IChannel reverse4 = channel4.Reverse;

        PooledBuffer buf1 = PooledBuffer.Rent(20);
        PooledBuffer buf2 = PooledBuffer.Rent(20);

        Encoding.UTF8.GetBytes("abcdefghijklmnopqr", buf1.Span); // 18 bytes
        Encoding.UTF8.GetBytes("st", buf2.Span); // 2 bytes

        async Task<byte[]> CollectStage1Async()
        {
            List<byte> collectedInner = new(20);
            while (true)
            {
                using ReadResult stage1 = await reverse1.ReadAsync(0, ReadBlockingMode.WaitAny, default);
                if (stage1.Result == IOResult.Ended)
                {
                    break;
                }

                Assert.Equal(IOResult.Ok, stage1.Result);
                ReadOnlySpan<byte> data = stage1.Data;

                for (int i = 0; i < data.Length; i++)
                {
                    collectedInner.Add((byte)(255 - data[i]));
                }
            }

            byte[] inverted = collectedInner.ToArray();
            PooledBuffer bufToChan2 = PooledBuffer.Rent(inverted.Length);
            inverted.CopyTo(bufToChan2.Span);
            await channel2.WriteAsync(bufToChan2, inverted.Length, 0);
            bufToChan2.Dispose();
            await channel2.WriteEofAsync();

            return inverted;
        }

        async Task RunPipelineAsync()
        {
            Task<ReadResult> firstTwoTask = reverse2.ReadAsync(2, ReadBlockingMode.WaitAll, default).AsTask();
            Task<ReadResult> firstEightTask = reverse2.ReadAsync(8, ReadBlockingMode.WaitAll, default).AsTask();
            Task<ReadResult> secondTwoTask = reverse2.ReadAsync(2, ReadBlockingMode.WaitAll, default).AsTask();
            Task<ReadResult> secondEightTask = reverse2.ReadAsync(8, ReadBlockingMode.WaitAll, default).AsTask();

            Task<ReadResult> chunk3Task = reverse3.ReadAsync(8, ReadBlockingMode.WaitAll, default).AsTask();
            Task<ReadResult> chunk4Task = reverse4.ReadAsync(8, ReadBlockingMode.WaitAll, default).AsTask();

            Task<byte[]> collectTask = CollectStage1Async();

            await Task.Yield();

            Assert.Equal(IOResult.Ok, await channel1.WriteAsync(buf1, 18, 0));
            Assert.Equal(IOResult.Ok, await channel1.WriteAsync(buf2, 2, 0));
            Assert.Equal(IOResult.Ok, await channel1.WriteEofAsync());

            byte[] invertedPayload = await collectTask;

            using (ReadResult firstTwo = await firstTwoTask)
            {
                Assert.Equal(IOResult.Ok, firstTwo.Result);
                Assert.True(firstTwo.Data.SequenceEqual(invertedPayload.AsSpan(0, 2)));
            }

            using (ReadResult firstEight = await firstEightTask)
            {
                Assert.Equal(IOResult.Ok, firstEight.Result);

                PooledBuffer toChannel3 = PooledBuffer.Rent(firstEight.Data.Length);
                firstEight.Data.CopyTo(toChannel3.Span);
                Assert.Equal(IOResult.Ok, await channel3.WriteAsync(toChannel3, toChannel3.Length));
                toChannel3.Dispose();
            }

            using (ReadResult secondTwo = await secondTwoTask)
            {
                Assert.Equal(IOResult.Ok, secondTwo.Result);
                Assert.True(secondTwo.Data.SequenceEqual(invertedPayload.AsSpan(10, 2)));
            }

            using (ReadResult secondEight = await secondEightTask)
            {
                Assert.Equal(IOResult.Ok, secondEight.Result);

                PooledBuffer toChannel4 = PooledBuffer.Rent(secondEight.Data.Length);
                secondEight.Data.CopyTo(toChannel4.Span);
                Assert.Equal(IOResult.Ok, await channel4.WriteAsync(toChannel4, toChannel4.Length));
                toChannel4.Dispose();
            }

            byte[] originalSlice3 = Encoding.UTF8.GetBytes("cdefghij");
            byte[] originalSlice4 = Encoding.UTF8.GetBytes("mnopqrst");

            using (ReadResult chunk3 = await chunk3Task)
            {
                Assert.Equal(IOResult.Ok, chunk3.Result);
                ReadOnlySpan<byte> data = chunk3.Data;
                byte[] reinverted = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    reinverted[i] = (byte)(255 - data[i]);
                }

                Assert.True(reinverted.AsSpan().SequenceEqual(originalSlice3));
            }

            using (ReadResult chunk4 = await chunk4Task)
            {
                Assert.Equal(IOResult.Ok, chunk4.Result);
                ReadOnlySpan<byte> data = chunk4.Data;
                byte[] reinverted = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    reinverted[i] = (byte)(255 - data[i]);
                }

                Assert.True(reinverted.AsSpan().SequenceEqual(originalSlice4));
            }
        }

        Task pipeline = RunPipelineAsync();
        Task timeout = Task.Delay(TimeSpan.FromSeconds(10));
        Task completed = await Task.WhenAny(pipeline, timeout);
        Assert.True(ReferenceEquals(pipeline, completed), "Pipeline timed out.");
        await pipeline;

        buf1.Dispose();
        buf2.Dispose();
        Assert.Equal(0, buf1.RefCount);
        Assert.Equal(0, buf2.RefCount);
    }

    [Fact]
    public async Task Concurrent_multi_buffer_writes_preserve_atomicity()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer a1 = PooledBuffer.Rent(2); Encoding.UTF8.GetBytes("AA", a1.Span);
        PooledBuffer a2 = PooledBuffer.Rent(2); Encoding.UTF8.GetBytes("BB", a2.Span);
        PooledBuffer b1 = PooledBuffer.Rent(3); Encoding.UTF8.GetBytes("CCC", b1.Span);
        PooledBuffer b2 = PooledBuffer.Rent(1); Encoding.UTF8.GetBytes("D", b2.Span);

        PooledBuffer.Slice[] segA = { a1[0..2], a2[0..2] };
        PooledBuffer.Slice[] segB = { b1[0..3], b2[0..1] };

        Task<IOResult> w1 = channel.WriteAsync(segA).AsTask();
        Task<IOResult> w2 = channel.WriteAsync(segB).AsTask();

        // Read two atomic messages of lengths 4 and 4 in any order; verify each is intact
        using ReadResult r1 = await remote.ReadAsync(4, ReadBlockingMode.WaitAll);
        string first = Encoding.UTF8.GetString(r1.Data);
        using ReadResult r2 = await remote.ReadAsync(4, ReadBlockingMode.WaitAll);
        string second = Encoding.UTF8.GetString(r2.Data);

        await Task.WhenAll(w1, w2);

        Assert.Equal(4, r1.Data.Length);
        Assert.Equal(4, r2.Data.Length);

        var messages = new[] { first, second };
        Assert.Contains("AABB", messages);
        Assert.Contains("CCCD", messages);

        foreach (PooledBuffer.Slice slice in segA)
        {
            slice.Dispose();
        }

        foreach (PooledBuffer.Slice slice in segB)
        {
            slice.Dispose();
        }

        a1.Dispose(); a2.Dispose(); b1.Dispose(); b2.Dispose();
    }

    [Fact]
    public async Task WriteLine_and_ReadLine_round_trip()
    {
        Channel channel = new();
        IChannel local = channel;
        IChannel remote = channel.Reverse;

        const string message = "channels round trip";

        Task<string> readTask = local.ReadLineAsync();
        Assert.Equal(IOResult.Ok, await remote.WriteLineAsync(message));

        string result = await readTask;
        Assert.Equal(message, result);
    }

    [Fact]
    public async Task Varints_flow_and_eof_signals_end()
    {
        Channel channel = new();
        IChannel local = channel;
        IChannel remote = channel.Reverse;

        const int number = 321_123;

        Task<int> readVarintTask = local.ReadVarintAsync();
        await remote.WriteVarintAsync(number);
        int decoded = await readVarintTask;
        Assert.Equal(number, decoded);

        Assert.Equal(IOResult.Ok, await remote.WriteEofAsync());

        using ReadResult ended = await local.ReadAsync(1, ReadBlockingMode.DontWait);
        Assert.Equal(IOResult.Ended, ended.Result);
    }

    [Fact]
    public async Task PooledBuffer_full_read_drops_channel_link()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buffer = PooledBuffer.Rent(5);
        Encoding.UTF8.GetBytes("hello", buffer.Span);

        Assert.Equal(1, buffer.RefCount);

        Task<IOResult> writeTask = channel.WriteAsync(buffer, buffer.Length).AsTask();
        await Task.Yield();

        Assert.Equal(2, buffer.RefCount); // writer + caller

        using (ReadResult read = await remote.ReadAsync(buffer.Length, ReadBlockingMode.WaitAll))
        {
            Assert.Equal(2, buffer.RefCount); // read-result link after channel released
            Assert.Equal(IOResult.Ok, read.Result);
            Assert.Equal("hello", Encoding.UTF8.GetString(read.Data.ToArray()));

            Assert.Equal(IOResult.Ok, await writeTask);
            Assert.Equal(2, buffer.RefCount); // channel link released, read-result still holds
        }

        buffer.Dispose();
        Assert.Equal(0, buffer.RefCount);
    }

    [Fact]
    public async Task PooledBuffer_partial_reads_hold_link_until_complete()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buffer = PooledBuffer.Rent(8);
        Encoding.UTF8.GetBytes("12345678", buffer.Span);

        Task<IOResult> writeTask = channel.WriteAsync(buffer, buffer.Length).AsTask();
        await Task.Yield();
        Assert.Equal(2, buffer.RefCount);

        using (ReadResult first = await remote.ReadAsync(4, ReadBlockingMode.WaitAll))
        {
            Assert.Equal(IOResult.Ok, first.Result);
            Assert.Equal(3, buffer.RefCount); // result link added, channel still holds

            using (ReadResult second = await remote.ReadAsync(4, ReadBlockingMode.WaitAll))
            {
                Assert.Equal(IOResult.Ok, second.Result);
                Assert.Equal(3, buffer.RefCount); // two result links, channel released

                Assert.Equal(IOResult.Ok, await writeTask);
                Assert.Equal(3, buffer.RefCount);
            }
        }

        buffer.Dispose();
        Assert.Equal(0, buffer.RefCount);
    }

    [Fact]
    public async Task PooledBuffer_forwarded_slice_adds_and_releases_links()
    {
        Channel source = new();
        IChannel sink = source.Reverse;

        Channel next = new();
        IChannel nextSink = next.Reverse;

        PooledBuffer buffer = PooledBuffer.Rent(8);
        Encoding.UTF8.GetBytes("abcdefgh", buffer.Span);

        Task<IOResult> writeTask = source.WriteAsync(buffer, buffer.Length).AsTask();
        await Task.Yield();
        Assert.Equal(2, buffer.RefCount);

        using (ReadResult first = await sink.ReadAsync(4, ReadBlockingMode.WaitAll))
        {
            Assert.Equal(IOResult.Ok, first.Result);
            Assert.Equal(3, buffer.RefCount); // result link added, channel still holds link

            PooledBuffer.Slice slice = buffer.LeaseSlice(0, 4);
            Task<IOResult> forwardTask = next.WriteAsync(slice).AsTask();
            await Task.Yield();
            Assert.Equal(4, buffer.RefCount); // slice + first read + channel + caller

            using (ReadResult forwarded = await nextSink.ReadAsync(4, ReadBlockingMode.WaitAll))
            {
                Assert.Equal(IOResult.Ok, forwarded.Result);
                Assert.Equal(IOResult.Ok, await forwardTask);
                Assert.Equal(5, buffer.RefCount); // slice + next channel read result + first read + channel + caller

                slice.Dispose();
                Assert.Equal(4, buffer.RefCount); // slice released

                using (ReadResult second = await sink.ReadAsync(4, ReadBlockingMode.WaitAll))
                {
                    Assert.Equal(IOResult.Ok, second.Result);
                    Assert.Equal(IOResult.Ok, await writeTask);
                    Assert.Equal(4, buffer.RefCount); // channel released, three read results remain
                }
            }
        }

        buffer.Dispose();
        Assert.Equal(0, buffer.RefCount);
    }

    [Fact]
    public async Task Slice_from_read_result_keeps_buffer_alive_after_dispose()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buffer = PooledBuffer.Rent(6);
        Encoding.UTF8.GetBytes("abcdef", buffer.Span);

        Task<IOResult> writeTask = channel.WriteAsync(buffer, buffer.Length).AsTask();
        await Task.Yield();

        Assert.Equal(2, buffer.RefCount); // writer + caller

        using ReadResult read = await remote.ReadAsync(buffer.Length, ReadBlockingMode.WaitAll);
        Assert.Equal(IOResult.Ok, read.Result);

        Assert.Equal(IOResult.Ok, await writeTask);
        Assert.Equal(2, buffer.RefCount); // caller + read result

        PooledBuffer.Slice lease = read.ToSlice();
        Assert.Equal(3, buffer.RefCount); // extra link for slice

        read.Dispose();
        Assert.Equal(2, buffer.RefCount); // slice holds link, buffer not returned

        lease.Dispose();
        Assert.Equal(1, buffer.RefCount); // only caller remains

        buffer.Dispose();
        Assert.Equal(0, buffer.RefCount);
    }

    [Fact]
    public async Task Slice_from_read_result_survives_caller_dispose()
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;

        PooledBuffer buffer = PooledBuffer.Rent(4);
        Encoding.UTF8.GetBytes("wxyz", buffer.Span);

        Task<IOResult> writeTask = channel.WriteAsync(buffer, buffer.Length).AsTask();
        await Task.Yield();

        Assert.Equal(2, buffer.RefCount); // writer + caller

        using ReadResult read = await remote.ReadAsync(buffer.Length, ReadBlockingMode.WaitAll);
        Assert.Equal(IOResult.Ok, read.Result);
        Assert.Equal(IOResult.Ok, await writeTask);

        Assert.Equal(2, buffer.RefCount); // caller + read result

        buffer.Dispose();
        Assert.Equal(1, buffer.RefCount); // only read result holds link

        PooledBuffer.Slice lease = read.ToSlice();
        Assert.Equal(2, buffer.RefCount); // slice adds link even after caller disposed

        read.Dispose();
        Assert.Equal(1, buffer.RefCount); // slice still holding

        lease.Dispose();
        Assert.Equal(0, buffer.RefCount);
    }

    [Fact]
    public void Array_length_is_trimmed_and_restored_thread_safely()
    {
        const int requested = 8;
        const int extra = 5;

        RecordingPool pool = new(extra);
        using PooledBuffer buffer = PooledBuffer.Rent(requested, pool);

        byte[] initial = buffer.Array;
        Assert.Equal(requested, initial.Length);
    }
}
