// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core.Tests;

public class ReaderTests
{
    [TestCase(new byte[] { 0x00 }, 0)]
    [TestCase(new byte[] { 0x7f }, 127)]
    [TestCase(new byte[] { 0x80, 0x01 }, 128)]
    [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 }, int.MaxValue)]
    [TestCase(new byte[] { 0x80, 0x00 }, 0)]
    [TestCase(new byte[] { 0x81, 0x80, 0x80, 0x80, 0x00 }, 1)]
    public async Task ReadVarintAcceptsValidValues(byte[] prefix, int expected)
    {
        IReader reader = new RecordingReader(prefix);

        Assert.That(await reader.ReadVarintAsync(), Is.EqualTo(expected));
    }

    [TestCase(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x08 })]
    [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x7f })]
    [TestCase(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x10 })]
    public void ReadVarintRejectsOverflow(byte[] prefix)
    {
        IReader reader = new RecordingReader(prefix);

        Assert.ThrowsAsync<FormatException>(async () => await reader.ReadVarintAsync());
    }

    [TestCase(new byte[] { 0x81, 0x08 })]
    [TestCase(new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 })]
    public void DefaultReadLineRejectsOversizedPrefixBeforeReadingPayload(byte[] prefix)
    {
        RecordingReader source = new(prefix);
        IReader reader = source;

        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync());
        Assert.That(source.RequestedLengths, Is.EqualTo(Enumerable.Repeat(1, prefix.Length)));
    }

    [Test]
    public async Task ReadVarintRejectsFiveContinuationBytesWithoutConsumingNextByte()
    {
        // Pins consumption only; ReadVarintRejectsOverflow covers the fifth-byte overflow guard.
        RecordingReader source = new([0x80, 0x80, 0x80, 0x80, 0x80, 0x2a]);
        IReader reader = source;

        Assert.ThrowsAsync<FormatException>(async () => await reader.ReadVarintAsync());
        Assert.That(source.RequestedLengths, Has.Count.EqualTo(5));
        Assert.That(await reader.ReadVarintAsync(), Is.EqualTo(42));
    }

    [Test]
    public async Task ReadVarintAcceptsFragmentedPrefix()
    {
        Channel channel = new();
        Task<int> read = channel.Reader.ReadVarintAsync();
        Task write = WriteFragments();
        try
        {
            await Task.WhenAll(read, write).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(await read, Is.EqualTo(int.MaxValue));
        }
        finally
        {
            await channel.AbortAsync();
            await Task.WhenAll(read, write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task WriteFragments()
        {
            foreach (byte value in new byte[] { 0xff, 0xff, 0xff, 0xff, 0x07 })
                await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new[] { value })).OrThrow();
        }
    }

    [Test]
    public async Task ReadVarintPreservesTruncatedInputFailure()
    {
        Channel channel = new();
        Task<int> read = channel.Reader.ReadVarintAsync();
        try
        {
            await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0x80 }));
            await channel.Reverse.WriteEofAsync();
            Assert.ThrowsAsync<ChannelClosedException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await channel.AbortAsync();
            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(1023)]
    [TestCase(1024)]
    public async Task DefaultReadLineAcceptsUpToLimit(int length)
    {
        string line = new('a', length - 1);
        IReader reader = new RecordingReader(Frame(line));

        Assert.That(await reader.ReadLineAsync(), Is.EqualTo(line));
    }

    [Test]
    public async Task ReadLineAcceptsExplicitLargerLimit()
    {
        string line = new('a', 1024);
        IReader reader = new RecordingReader(Frame(line));

        Assert.That(await reader.ReadLineAsync(1025), Is.EqualTo(line));
    }

    [Test]
    public void ReadLineRejectsExplicitLimitBeforeReadingPayload()
    {
        RecordingReader source = new([0x05]);
        IReader reader = source;

        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync(4));
        Assert.That(source.RequestedLengths, Is.EqualTo(new[] { 1 }));
    }

    [Test]
    public void ReadLineRejectsNegativeLimitBeforeReadingInput()
    {
        RecordingReader source = new([0x00]);
        IReader reader = source;

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await reader.ReadLineAsync(-1));
        Assert.That(source.RequestedLengths, Is.Empty);
    }

    [Test]
    public async Task ReadLinePreservesEmptyPayload()
    {
        IReader reader = new RecordingReader([0x00]);

        Assert.That(await reader.ReadLineAsync(0), Is.Empty);
    }

    [Test]
    public async Task ReadLineCountsUtf8BytesIncludingNewline()
    {
        IReader reader = new RecordingReader(Frame("é"));
        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync(2));

        reader = new RecordingReader(Frame("é"));
        Assert.That(await reader.ReadLineAsync(3), Is.EqualTo("é"));
    }

    [Test]
    public async Task ReadLinePreservesDecodingAndNewlineTrimming()
    {
        IReader reader = new RecordingReader([0x04, 0xff, 0x78, 0x0a, 0x0a]);

        Assert.That(await reader.ReadLineAsync(4), Is.EqualTo("�x"));
    }

    [Test]
    public async Task ReadLineHandlesFragmentedPayloadAndAdjacentFrame()
    {
        Channel channel = new();
        Task<string> read = channel.Reader.ReadLineAsync(3);
        Task write = WriteFragments();
        try
        {
            Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo("é"));
            Assert.That(await channel.Reader.ReadLineAsync(2).WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo("x"));
            await write.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await channel.AbortAsync();
            await Task.WhenAll(read, write).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        async Task WriteFragments()
        {
            await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0x03, 0xc3 })).OrThrow();
            await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0xa9 })).OrThrow();
            await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0x0a, 0x02, 0x78, 0x0a })).OrThrow();
        }
    }

    [Test]
    public async Task ReadLineForwardsTokenToEveryRead()
    {
        using CancellationTokenSource cancellation = new();
        RecordingReader source = new(Frame(new string('a', 127)));
        IReader reader = source;

        await reader.ReadLineAsync(128, cancellation.Token);

        Assert.That(source.RequestedLengths, Is.EqualTo(new[] { 1, 1, 128 }));
        Assert.That(source.Tokens, Is.All.EqualTo(cancellation.Token));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadLineCancellationInterruptsPrefixAndPayload(bool afterPrefix)
    {
        Channel channel = new();
        using CancellationTokenSource cancellation = new();
        Task<string> read = channel.Reader.ReadLineAsync(1024, cancellation.Token);
        try
        {
            // Completion of this write proves the prefix was consumed before cancellation.
            if (afterPrefix) await channel.Reverse.WriteVarintAsync(4);
            cancellation.Cancel();

            Assert.ThrowsAsync<ChannelClosedException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            await channel.AbortAsync();
            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static byte[] Frame(string line)
    {
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
        byte[] frame = new byte[VarInt.GetSizeInBytes(payload.Length) + payload.Length];
        int offset = 0;
        VarInt.Encode(payload.Length, frame, ref offset);
        payload.CopyTo(frame, offset);
        return frame;
    }

    private sealed class RecordingReader(byte[] bytes) : IReader
    {
        private int _offset;
        public List<int> RequestedLengths { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            RequestedLengths.Add(length);
            Tokens.Add(token);
            token.ThrowIfCancellationRequested();
            if (length > bytes.Length - _offset) return ValueTask.FromResult(ReadResult.Ended);
            ReadOnlySequence<byte> data = new(bytes.AsMemory(_offset, length));
            _offset += length;
            return ValueTask.FromResult(new ReadResult { Result = IOResult.Ok, Data = data });
        }
    }
}
