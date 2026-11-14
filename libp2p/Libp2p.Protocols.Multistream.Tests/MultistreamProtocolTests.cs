// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class MultistreamProtocolTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ResetBeforeNegotiationReplyFailsThePendingRequest(bool afterHello)
    {
        Channel channel = new();
        IChannel remote = channel.Reverse;
        IConnectionContext context = Substitute.For<IConnectionContext>();
        IProtocol selected = Substitute.For<IProtocol>();
        selected.Id.Returns("/test/refused");
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        context.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = selected, CompletionSource = completion });
        MultistreamProtocol protocol = new();
        Task negotiation = protocol.DialAsync(channel, context);
        try
        {
            Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(protocol.Id));
            Assert.That(await remote.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(selected.Id));
            if (afterHello) await remote.WriteLineAsync(protocol.Id);
            await remote.AbortAsync();
            await negotiation.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(completion.Task.IsFaulted, Is.True, "A reset before handler dispatch must fail the pending dial request.");
            Assert.That(completion.Task.Exception!.InnerException, Is.TypeOf<ChannelClosedException>());
            await context.DidNotReceiveWithAnyArgs().Upgrade(default!, default(IProtocol)!);
        }
        finally
        {
            await remote.AbortAsync();
            await negotiation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        peerContext.UpgradeOptions.Returns(new UpgradeOptions());

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        peerContext.SubProtocols.Returns([proto1]);
        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>()).Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("proto1");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));

        await dialTask;

        _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake_With_SpecificRequest()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        peerContext.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = proto1 });

        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>()).Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("proto1");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));

        await dialTask;

        _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_SpecificRequest_IsProposedBeforeRemoteHello()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        peerContext.UpgradeOptions.Returns(new UpgradeOptions { SelectedProtocol = proto1 });
        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>()).Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto1.Id));

        await downChannel.WriteLineAsync(proto.Id);
        await downChannel.WriteLineAsync(proto1.Id);
        await dialTask;

        ReadResult extraWrite = await downChannel.ReadAsync(0, ReadBlockingMode.DoNotWait);
        Assert.That(extraWrite.Data.Length, Is.Zero);
        _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForUnknownProtocol()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        peerContext.UpgradeOptions.Returns(new UpgradeOptions() { SelectedProtocol = null });

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        peerContext.SubProtocols.Returns([proto1]);

        MultistreamProtocol proto = new();
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("proto2");
        });

        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));

        await dialTask;

        _ = peerContext.DidNotReceive().Upgrade(downChannelFromProtocolPov, proto1);
    }

    [Test]
    public async Task Test_ConnectionEstablished_ForAnyOfProtocols()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        peerContext.UpgradeOptions.Returns(new UpgradeOptions());

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto2.Id.Returns("proto2");
        peerContext.SubProtocols.Returns([proto1, proto2]);
        IChannel upChannel = new TestChannel();
        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("na");
            await downChannel.WriteLineAsync("proto2");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto1.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto2.Id));

        await dialTask;

        _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto2);
        await upChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForBadProtocol()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        peerContext.UpgradeOptions.Returns(new UpgradeOptions());

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto2");
        peerContext.SubProtocols.Returns([proto1, proto2]);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        _ = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync("na1");
            await downChannel.WriteLineAsync("proto2");
        });

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto1.Id));

        await dialTask;

        _ = peerContext.DidNotReceiveWithAnyArgs().Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>());
    }

    [Test]
    public async Task Test_FirstListedProtocol_IsProposedBeforeRemoteHello()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        peerContext.UpgradeOptions.Returns(new UpgradeOptions());
        IProtocol proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        peerContext.SubProtocols.Returns([proto1]);
        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>()).Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        try
        {
            Assert.That(await downChannel.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(proto.Id));
            Assert.That(await downChannel.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(proto1.Id),
                "The first proposal must not wait a round trip for the remote hello.");

            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync(proto1.Id);
            await dialTask.WaitAsync(TimeSpan.FromSeconds(2));
            _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto1);
        }
        finally
        {
            await downChannel.CloseAsync();
            await dialTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Test_HelloAndFirstProposal_AreOneWrite(bool selectedUpfront)
    {
        IChannel downChannel = new TestChannel();
        WriteCountingChannel downChannelFromProtocolPov = new(((TestChannel)downChannel).Reverse());
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        IProtocol proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        peerContext.UpgradeOptions.Returns(selectedUpfront ? new UpgradeOptions { SelectedProtocol = proto1 } : new UpgradeOptions());
        peerContext.SubProtocols.Returns([proto1]);
        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>()).Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        Task remoteReplies = Task.Run(async () =>
        {
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync(proto1.Id);
        });

        try
        {
            Assert.That(await downChannel.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(proto.Id));
            Assert.That(await downChannel.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(proto1.Id));
            await Task.WhenAll(dialTask, remoteReplies).WaitAsync(TimeSpan.FromSeconds(2));

            Assert.That(downChannelFromProtocolPov.Writes, Is.EqualTo(1));
            _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto1);
        }
        finally
        {
            await downChannel.CloseAsync();
            await Task.WhenAll(dialTask, remoteReplies).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task Test_PipelinedProposal_KeepsNonAsciiProtocolId()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IConnectionContext peerContext = Substitute.For<IConnectionContext>();
        peerContext.UpgradeOptions.Returns(new UpgradeOptions());
        IProtocol proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("/\u00e9t\u00e9/1.0.0");
        peerContext.SubProtocols.Returns([proto1]);
        peerContext.Upgrade(Arg.Any<IChannel>(), Arg.Any<IProtocol>()).Returns(Task.CompletedTask);

        MultistreamProtocol proto = new();
        Task dialTask = proto.DialAsync(downChannelFromProtocolPov, peerContext);
        try
        {
            Assert.That(await downChannel.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(proto.Id));
            Assert.That(await downChannel.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(proto1.Id));
            await downChannel.WriteLineAsync(proto.Id);
            await downChannel.WriteLineAsync(proto1.Id);
            await dialTask.WaitAsync(TimeSpan.FromSeconds(2));
            _ = peerContext.Received().Upgrade(downChannelFromProtocolPov, proto1);
        }
        finally
        {
            await downChannel.CloseAsync();
            await dialTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class WriteCountingChannel(IChannel inner) : IChannel
    {
        private int _writes;
        public int Writes => Volatile.Read(ref _writes);
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
            => inner.ReadAsync(length, blockingMode, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            Interlocked.Increment(ref _writes);
            return inner.WriteAsync(bytes, token);
        }
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
    }
}
