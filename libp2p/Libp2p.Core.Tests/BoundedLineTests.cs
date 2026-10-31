// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core.Exceptions;

namespace Nethermind.Libp2p.Core.Tests;

public class BoundedLineTests
{
    [Test]
    public void BoundedReadRejectsOversizedPrefixBeforeRequestingPayload()
    {
        RecordingReader recording = new();
        IReader reader = recording;
        Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadLineAsync(1024));
        Assert.That(recording.Lengths, Is.EqualTo(new[] { 1, 1 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BoundedReadCancelsTheActualPrefixAndPayloadWait(bool payload)
    {
        Channel channel = new();
        using CancellationTokenSource cancellation = new();
        Task<string> read = ((IReader)channel).ReadLineAsync(1024, cancellation.Token);
        try
        {
            if (payload) await channel.Reverse.WriteVarintAsync(3);
            cancellation.Cancel();
            Assert.ThrowsAsync<ChannelClosedException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(2)));
            // The cancelled operation relinquishes the reader, allowing another read to proceed.
            Task<ReadResult> next = channel.ReadAsync(1).AsTask();
            await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 42 }));
            Assert.That((await next).Data.ToArray(), Is.EqualTo(new byte[] { 42 }));
        }
        finally
        {
            await channel.AbortAsync();
            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(1023, 1024)]
    [TestCase(1023, null)]
    [TestCase(1024, 1025)]
    public async Task ExplicitAndDefaultBoundsAcceptTheirPayloadBoundary(int characters, int? maxLength)
    {
        Channel channel = new();
        IReader reader = channel;
        string expected = new('a', characters);
        Task<string> read = maxLength is { } limit ? reader.ReadLineAsync(limit) : reader.ReadLineAsync();
        try
        {
            await channel.Reverse.WriteLineAsync(expected);
            Assert.That(await read, Is.EqualTo(expected));
        }
        finally
        {
            await channel.AbortAsync();
            await ((Task)read).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class RecordingReader : IReader
    {
        // Varint encoding of 1,025. No payload is present or needed to reject the message.
        private readonly byte[] _prefix = [0x81, 0x08];
        private int _offset;
        public List<int> Lengths { get; } = [];
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode mode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            Lengths.Add(length);
            Assert.That(length, Is.EqualTo(1), "The payload must never be requested.");
            return ValueTask.FromResult(ReadResult.Ok(new ReadOnlySequence<byte>(_prefix.AsMemory(_offset++, 1))));
        }
    }
}
