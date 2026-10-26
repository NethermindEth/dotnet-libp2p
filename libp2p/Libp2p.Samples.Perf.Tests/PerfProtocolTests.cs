// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using DataTransferBenchmark;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Samples.Perf.Tests;

[TestFixture]
public class PerfProtocolTests
{
    [Test]
    public async Task DialSendsDownloadSizeAndEndsUploadBeforeReadingResponse()
    {
        PerfProtocol.BytesToSend = 3;
        PerfProtocol.BytesToReceive = 2;
        TestChannel channel = new();
        IChannel remote = channel.Reverse();

        Task dial = new PerfProtocol().DialAsync(channel, null!);

        ReadOnlySequence<byte> header = await remote.ReadAsync(8).OrThrow();
        Assert.That(BinaryPrimitives.ReadUInt64BigEndian(header.ToArray()), Is.EqualTo(2UL));
        ReadOnlySequence<byte> upload = await remote.ReadAsync(3).OrThrow();
        Assert.That(upload.ToArray(), Is.EqualTo(new byte[] { 0xAB, 0xAB, 0xAB }));
        Assert.That((await remote.ReadAsync(0, ReadBlockingMode.WaitAny)).Result, Is.EqualTo(IOResult.Ended));

        await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[2]));
        await remote.WriteEofAsync();
        await dial;
    }

    [Test]
    public async Task ListenWaitsForUploadEofAndSendsRequestedBytes()
    {
        TestChannel channel = new();
        IChannel remote = channel.Reverse();
        Task listen = new PerfProtocol().ListenAsync(channel, null!);

        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(header, 5);
        await remote.WriteAsync(new ReadOnlySequence<byte>(header));
        await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[7]));
        await remote.WriteEofAsync();

        ReadOnlySequence<byte> response = await remote.ReadAsync(5).OrThrow();
        Assert.That(response.ToArray(), Is.EqualTo(new byte[] { 0xAB, 0xAB, 0xAB, 0xAB, 0xAB }));
        Assert.That((await remote.ReadAsync(0, ReadBlockingMode.WaitAny)).Result, Is.EqualTo(IOResult.Ended));
        await listen;
    }

    [Test]
    public async Task DialRejectsMoreBytesThanRequested()
    {
        PerfProtocol.BytesToSend = 0;
        PerfProtocol.BytesToReceive = 1;
        TestChannel channel = new();
        IChannel remote = channel.Reverse();
        Task dial = new PerfProtocol().DialAsync(channel, null!);

        await remote.ReadAsync(8).OrThrow();
        Assert.That((await remote.ReadAsync(0, ReadBlockingMode.WaitAny)).Result, Is.EqualTo(IOResult.Ended));
        await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[2]));
        await remote.WriteEofAsync();

        Assert.ThrowsAsync<InvalidDataException>(async () => await dial);
    }
}
