// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Ping.Tests;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class PingProtocolTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task ListenAsync_WaitsForCompletePayload_WhenPingIsFragmented()
    {
        byte[] ping = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        byte[][] replies = await EchoAsync(ping[..10], ping[10..]);

        Assert.That(replies, Has.Length.EqualTo(1));
        Assert.That(replies[0], Is.EqualTo(ping));
    }

    [Test]
    public async Task ListenAsync_DoesNotEchoIncompletePayload_AtEof()
    {
        byte[][] replies = await EchoAsync(new byte[10]);

        Assert.That(replies, Is.Empty);
    }

    [Test]
    public async Task ListenAsync_EchoesEachPayload_WhenPingsAreCoalesced()
    {
        byte[] pings = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

        byte[][] replies = await EchoAsync(pings);

        Assert.That(replies, Has.Length.EqualTo(2));
        Assert.That(replies[0], Is.EqualTo(pings[..32]));
        Assert.That(replies[1], Is.EqualTo(pings[32..]));
    }

    [Test]
    public async Task ListenAsync_ExitsWithoutReply_AtEof()
    {
        byte[][] replies = await EchoAsync();

        Assert.That(replies, Is.Empty);
    }

    private static async Task<byte[][]> EchoAsync(params byte[][] chunks)
    {
        Channel input = new();
        IChannel channel = Substitute.For<IChannel>();
        channel.ReadAsync(Arg.Any<int>(), Arg.Any<ReadBlockingMode>(), Arg.Any<CancellationToken>())
            .Returns(call => input.ReadAsync(call.ArgAt<int>(0), call.ArgAt<ReadBlockingMode>(1), call.ArgAt<CancellationToken>(2)));

        // Record protocol writes individually so a dialer's buffering cannot hide partial replies.
        List<byte[]> replies = [];
        channel.WriteAsync(Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                replies.Add(call.ArgAt<ReadOnlySequence<byte>>(0).ToArray());
                return new ValueTask<IOResult>(IOResult.Ok);
            });

        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = "/ip4/127.0.0.1/tcp/1234" });
        using CancellationTokenSource timeout = new(Timeout);
        Task listener = new PingProtocol().ListenAsync(channel, context);
        try
        {
            foreach (byte[] chunk in chunks)
            {
                Assert.That(await input.Reverse.WriteAsync(new ReadOnlySequence<byte>(chunk), timeout.Token), Is.EqualTo(IOResult.Ok));
            }

            Assert.That(await input.Reverse.WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
            await listener.WaitAsync(timeout.Token);
            return replies.ToArray();
        }
        finally
        {
            await input.CloseAsync();
            await listener.WaitAsync(Timeout);
        }
    }
}
