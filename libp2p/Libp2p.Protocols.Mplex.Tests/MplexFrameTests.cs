// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Mplex;
using NUnit.Framework;
using System.Buffers;

namespace Nethermind.Libp2p.Protocols.Mplex.Tests;

internal class MplexFrameTests
{
    [TestCase("0000", 0UL, MplexFlag.NewStream, 0)]
    [TestCase("0103", 0UL, MplexFlag.MessageReceiver, 3)]
    [TestCase("82018040", 16UL, MplexFlag.MessageInitiator, 8192)]
    [TestCase("FBFFFFFFFFFFFFFF7F00", MplexFrame.MaxStreamId, MplexFlag.CloseReceiver, 0)]
    public async Task DecodesWireHeaders(string hex, ulong streamId, MplexFlag flag, int length)
    {
        BytesReader reader = new(Convert.FromHexString(hex));
        Assert.That(await MplexFrame.ReadAsync(reader), Is.EqualTo(new MplexFrame(streamId, flag, length)));
        Assert.That(reader.Offset, Is.EqualTo(hex.Length / 2));
    }

    [TestCase("0700")]
    [TestCase("00818040")]
    [TestCase("0401")]
    [TestCase("0501")]
    [TestCase("800000")]
    [TestCase("008000")]
    [TestCase("8080808080808080800000")]
    [TestCase("0080808080808080808000")]
    public void RejectsInvalidHeadersBeforeReadingABody(string hex)
    {
        BytesReader reader = new(Convert.FromHexString(hex));
        Assert.ThrowsAsync<InvalidDataException>(async () => await MplexFrame.ReadAsync(reader));
        Assert.That(reader.LargestRead, Is.EqualTo(1));
    }

    [Test]
    public void EncodesExactFrameBytes()
    {
        MplexFrame frame = new(16, MplexFlag.MessageInitiator, 3);
        Assert.That(frame.Encode(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 })),
            Is.EqualTo(Convert.FromHexString("820103010203")));
    }

    [TestCase(MplexFrame.MaxStreamId + 1)]
    [TestCase(ulong.MaxValue)]
    public void RejectsOutboundStreamIdOverflow(ulong id)
    {
        Assert.Throws<InvalidDataException>(() => new MplexFrame(id, MplexFlag.NewStream, 0).Encode(default));
    }

    [TestCase(1, 0)]
    [TestCase(0, 1)]
    public void OutboundLengthMustMatchPayload(int declared, int actual)
    {
        Assert.Throws<InvalidDataException>(() => new MplexFrame(0, MplexFlag.MessageInitiator, declared)
            .Encode(new ReadOnlySequence<byte>(new byte[actual])));
    }

    [Test]
    public async Task AcceptsMaximumPayloadLength()
    {
        Assert.That(await MplexFrame.ReadAsync(new BytesReader(Convert.FromHexString("02808040"))),
            Is.EqualTo(new MplexFrame(0, MplexFlag.MessageInitiator, 1024 * 1024)));
    }

    private sealed class BytesReader(byte[] bytes) : IReader
    {
        public int Offset { get; private set; }
        public int LargestRead { get; private set; }

        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            LargestRead = Math.Max(LargestRead, length);
            if (length > bytes.Length - Offset) return ValueTask.FromResult(ReadResult.Ended);
            ReadResult result = new() { Result = IOResult.Ok, Data = new ReadOnlySequence<byte>(bytes.AsMemory(Offset, length)) };
            Offset += length;
            return ValueTask.FromResult(result);
        }
    }
}
