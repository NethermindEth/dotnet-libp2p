// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class YamuxUnreadRequestTests
{
    [TestCase(13, 57, false, false)]
    [TestCase(13, 57, false, true)]
    [TestCase(255, 57, true, false)]
    [TestCase(256, 57, true, true)]
    [TestCase(257, 8193, false, false)]
    [TestCase(4097, 8193, false, true)]
    [TestCase(65535, 57, true, false)]
    [TestCase(65536, 57, true, true)]
    [TestCase(65537, 8193, false, false)]
    [TestCase(262145, 57, false, false)]
    public async Task ListenerReturnPreservesCompleteReply(int requestLength, int replyLength,
        bool halfCloseRequest, bool oneByteReads)
    {
        await using Setup test = new();
        await test.AddPeersAsync(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);
        byte[] reply = await session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(
            new RequestCase(requestLength, replyLength, halfCloseRequest, oneByteReads), timeout.Token).WaitAsync(timeout.Token);

        Assert.That(reply, Is.EqualTo(UnreadRequestProtocol.ExpectedReply(replyLength, 0)));
    }

    [Test]
    public async Task ConcurrentStreamsKeepRepliesIsolated()
    {
        await using Setup test = new();
        await test.AddPeersAsync(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        RequestCase[] requests =
        [
            new(13, 57, false, true, 1),
            new(4097, 8193, true, false, 2),
            new(65536, 57, false, true, 3)
        ];
        Task<byte[]>[] replies = requests.Select(request =>
            session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(request, timeout.Token)).ToArray();
        byte[][] received = await Task.WhenAll(replies).WaitAsync(timeout.Token);

        for (int i = 0; i < requests.Length; i++)
            Assert.That(received[i], Is.EqualTo(UnreadRequestProtocol.ExpectedReply(requests[i].ReplyLength, requests[i].Seed)));
    }

    [Test]
    public async Task ListenerExceptionResetsOnlyItsStream()
    {
        await using Setup test = new();
        await test.AddPeersAsync(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        Assert.CatchAsync<ChannelAbortedException>(async () =>
            await session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(
                new RequestCase(257, 57, false, true, 0, true), timeout.Token).WaitAsync(timeout.Token));

        byte[] reply = await session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(
            new RequestCase(257, 57, false, true), timeout.Token).WaitAsync(timeout.Token);
        Assert.That(reply, Is.EqualTo(UnreadRequestProtocol.ExpectedReply(57, 0)));
    }

    [Test]
    public async Task ExplicitAbortAfterPartialReplyRemainsAnAbort()
    {
        await using Setup test = new();
        await test.AddPeersAsync(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        Assert.CatchAsync<ChannelAbortedException>(async () =>
            await session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(
                new RequestCase(257, 57, false, true, 0, false, true), timeout.Token).WaitAsync(timeout.Token));
    }

    [Test]
    public async Task CancellationAfterRequestHalfCloseResetsOnlyThatStream()
    {
        await using Setup test = new();
        await test.AddPeersAsync(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using CancellationTokenSource cancelled = new();
        ISession session = await test.Peers[0].DialAsync([.. test.Peers[1].ListenAddresses]);

        Task<byte[]> pending = session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(
            new RequestCase(257, 57, true, true, 0, false, false, true), cancelled.Token);
        IChannel listenerChannel = await test.Protocol.ListenerWaiting.Task.WaitAsync(timeout.Token);
        try
        {
            cancelled.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(timeout.Token));
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            listenerChannel.GetAwaiter().OnCompleted(() => closed.TrySetResult());
            await closed.Task.WaitAsync(timeout.Token);
            Assert.That((await listenerChannel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        }
        finally
        {
            test.Protocol.ReleaseListener.TrySetResult();
        }

        byte[] reply = await session.DialAsync<UnreadRequestProtocol, RequestCase, byte[]>(
            new RequestCase(257, 57, false, true), timeout.Token).WaitAsync(timeout.Token);
        Assert.That(reply, Is.EqualTo(UnreadRequestProtocol.ExpectedReply(57, 0)));
    }

    private sealed class Setup : E2eTestSetup
    {
        public UnreadRequestProtocol Protocol { get; } = new();
        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];

        protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
            => builder.AddProtocol(Protocol);
    }

    public sealed record RequestCase(int Length, int ReplyLength, bool HalfClose, bool OneByteReads,
        int Seed = 0, bool ThrowListener = false, bool AbortAfterPartial = false, bool HoldListener = false);

    public sealed class UnreadRequestProtocol : ISessionProtocol<RequestCase, byte[]>
    {
        public TaskCompletionSource<IChannel> ListenerWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseListener { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static byte[] ExpectedReply(int length, int seed) =>
            Enumerable.Range(0, length).Select(i => (byte)(i * 17 + 3 + seed)).ToArray();
        public string Id => "/unread-request/1.0.0";

        public async Task<byte[]> DialAsync(IChannel channel, ISessionContext context, RequestCase request)
        {
            CancellationToken token = context.UpgradeOptions?.CancellationToken ?? default;
            byte[] bytes = new byte[request.Length];
            bytes[0] = (byte)((request.HalfClose ? 1 : 0) | (request.ReplyLength == 57 ? 2 : 0) |
                (request.Seed << 2) | (request.ThrowListener ? 0x80 : 0) |
                (request.AbortAfterPartial ? 0x40 : 0) | (request.HoldListener ? 0x20 : 0));
            Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(bytes)), Is.EqualTo(IOResult.Ok));
            if (request.HalfClose)
                Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));

            using MemoryStream received = new();
            while (true)
            {
                ReadResult read = await channel.ReadAsync(request.OneByteReads ? 1 : 0,
                    ReadBlockingMode.WaitAny, token);
                switch (read.Result)
                {
                    case IOResult.Ok:
                        foreach (ReadOnlyMemory<byte> segment in read.Data)
                            received.Write(segment.Span);
                        Assert.That(received.Length, Is.LessThanOrEqualTo(request.ReplyLength));
                        break;
                    case IOResult.Ended:
                        return received.ToArray();
                    case IOResult.Aborted:
                        throw new ChannelAbortedException();
                    case IOResult.Cancelled:
                        throw new OperationCanceledException(token);
                    default:
                        Assert.Fail($"Unexpected reply read status: {read.Result}");
                        break;
                }
            }
        }

        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            ReadResult firstByte = await channel.ReadAsync(1);
            Assert.That(firstByte.Result, Is.EqualTo(IOResult.Ok));
            byte flags = firstByte.Data.FirstSpan[0];
            if ((flags & 0x80) != 0)
                throw new InvalidOperationException("Listener failed after receiving an incomplete request.");
            if ((flags & 0x20) != 0)
            {
                Assert.That((await channel.ReadAsync(256)).Result, Is.EqualTo(IOResult.Ok));
                Assert.That((await channel.ReadAsync(0, ReadBlockingMode.WaitAny)).Result, Is.EqualTo(IOResult.Ended));
                ListenerWaiting.TrySetResult(channel);
                await ReleaseListener.Task;
                return;
            }
            int replyLength = (flags & 2) == 0 ? 8193 : 57;
            byte[] reply = ExpectedReply(replyLength, (flags & 0x3c) >> 2);
            if ((flags & 0x40) != 0)
            {
                Assert.That((await channel.ReadAsync(256)).Result, Is.EqualTo(IOResult.Ok));
                Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(reply.AsMemory(0, 7))), Is.EqualTo(IOResult.Ok));
                await ((Channel)channel).AbortAsync();
                return;
            }
            Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(reply)), Is.EqualTo(IOResult.Ok));
            if ((flags & 1) != 0)
                Assert.That(await channel.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        }
    }
}
