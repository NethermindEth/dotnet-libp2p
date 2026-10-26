// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using NSubstitute;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class DataChannelOverIChannelTests
{
    [Test]
    public async Task AbortRemainsDistinctFromGracefulClose()
    {
        RTCDataChannel dataChannel = Substitute.For<RTCDataChannel>(null!, new RTCDataChannelInit());
        DataChannelOverIChannel channel = new(dataChannel);
        Task<ReadResult> pendingRead = channel.ReadAsync(1).AsTask();

        await channel.AbortAsync();

        Assert.That((await pendingRead.WaitAsync(TimeSpan.FromSeconds(2))).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That((await channel.ReadAsync(0, ReadBlockingMode.DoNotWait)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })), Is.EqualTo(IOResult.Aborted));
        Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Aborted));
    }
}
