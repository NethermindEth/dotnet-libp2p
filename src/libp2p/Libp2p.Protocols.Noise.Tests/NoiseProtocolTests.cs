// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
public class NoiseProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        // IChannel downChannel = new TestChannel();
        // IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        // IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        // IPeerContext peerContext = Substitute.For<IPeerContext>();
        //
        // IDuplexProtocol? proto1 = Substitute.For<IDuplexProtocol>();
        // proto1.Id.Returns("proto1");
        // channelFactory.SubProtocols.Returns(new[] { proto1 });
        // IChannel upChannel = new TestChannel();
        // channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IDuplexProtocol>())
        //     .Returns(upChannel);
        //
        // NoiseProtocol proto = new();
        // _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        // await downChannel.WriteLineAsync(proto.Id);
        // await downChannel.WriteLineAsync("proto1");
        //
        // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        // channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        // await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForBrokenHandshake()
    {
        // IChannel downChannel = new TestChannel();
        // IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        // IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        // IPeerContext peerContext = Substitute.For<IPeerContext>();
        //
        // IDuplexProtocol? proto1 = Substitute.For<IDuplexProtocol>();
        // proto1.Id.Returns("proto1");
        // channelFactory.SubProtocols.Returns(new[] { proto1 });
        // IChannel upChannel = new TestChannel();
        // channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IDuplexProtocol>())
        //     .Returns(upChannel);
        //
        // NoiseProtocol proto = new();
        // _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        // await downChannel.WriteLineAsync(proto.Id);
        // await downChannel.WriteLineAsync("proto2");
        //
        // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        // channelFactory.DidNotReceive().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        // await upChannel.CloseAsync();
    }
}
