// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Buffers;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class PubsubFrameLimitTests
{
    [Test]
    public void MaxRpcBytes_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PubsubSettings { MaxRpcBytes = -1 });
    }

    [TestCase(1_025UL)]
    [TestCase(2_147_483_648UL)]
    [TestCase(4_294_967_295UL)]
    public async Task ReadPrefixedProtobufAsync_RejectsFramesLargerThanLimit(ulong messageLength)
    {
        TestChannel channel = new();
        Task write = channel.Reverse().WriteAsync(new ReadOnlySequence<byte>(EncodeVarint(messageLength))).AsTask();
        IChannel reader = channel;

        InvalidDataException? exception = Assert.ThrowsAsync<InvalidDataException>(async () =>
            await reader.ReadPrefixedProtobufAsync(Rpc.Parser, 1_024));

        Assert.That(exception!.Message, Does.Contain(messageLength.ToString()));
        await write;
    }

    [Test]
    public async Task ReadPrefixedProtobufAsync_RejectsOverflowingVarint()
    {
        byte[] overflowedLength = [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x02];
        TestChannel channel = new();
        Task write = channel.Reverse().WriteAsync(new ReadOnlySequence<byte>(overflowedLength)).AsTask();
        IChannel reader = channel;

        Assert.ThrowsAsync<FormatException>(async () => await reader.ReadPrefixedProtobufAsync(Rpc.Parser, 1_024));
        await write;
    }

    private static byte[] EncodeVarint(ulong value)
    {
        byte[] bytes = new byte[VarInt.GetSizeInBytes(value)];
        int offset = 0;
        VarInt.Encode(value, bytes, ref offset);
        return bytes;
    }
}
