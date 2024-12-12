// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class MultistreamProtocolTests
{
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
        peerContext.UpgradeOptions.Returns(new UpgradeOptions());

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
}
