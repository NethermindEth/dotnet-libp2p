// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Multistream.Tests;

[TestFixture]
public class MultistreamProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        TestChannel downChannel = new();
        IChannel downChannelFromProtocolPov = downChannel.Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelFactory.SubProtocols.Returns(new[] { proto1 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(upChannel);

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        await downChannel.Writer.WriteLineAsync(proto.Id);
        await downChannel.Writer.WriteLineAsync("proto1");

        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo("proto1"));
        channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForUnknownProtocol()
    {
        TestChannel downChannel = new();
        IChannel downChannelFromProtocolPov = downChannel.Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelFactory.SubProtocols.Returns(new[] { proto1 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(upChannel);

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        await downChannel.Writer.WriteLineAsync(proto.Id);
        await downChannel.Writer.WriteLineAsync("proto2");

        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo("proto1"));
        channelFactory.DidNotReceive().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        await upChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionEstablished_ForAnyOfProtocols()
    {
        TestChannel downChannel = new();
        IChannel downChannelFromProtocolPov = downChannel.Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto2.Id.Returns("proto2");
        channelFactory.SubProtocols.Returns(new[] { proto1, proto2 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(upChannel);

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        await downChannel.Writer.WriteLineAsync(proto.Id);
        await downChannel.Writer.WriteLineAsync("na");
        await downChannel.Writer.WriteLineAsync("proto2");

        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto1.Id));
        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto2.Id));
        channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto2);
        await upChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForBadProtocol()
    {
        TestChannel downChannel = new();
        IChannel downChannelFromProtocolPov = downChannel.Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto2");
        channelFactory.SubProtocols.Returns(new[] { proto1, proto2 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(upChannel);

        MultistreamProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        await downChannel.Writer.WriteLineAsync(proto.Id);
        await downChannel.Writer.WriteLineAsync("na1");
        await downChannel.Writer.WriteLineAsync("proto2");

        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto1.Id));
        Assert.That(await downChannel.Reader.ReadLineAsync(), Is.EqualTo(proto2.Id));
        channelFactory.DidNotReceiveWithAnyArgs().SubDialAndBind(null, null, (IProtocol)null);
        await upChannel.CloseAsync();
    }
}
