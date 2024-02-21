// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using System.Buffers;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
public class YamuxProtocolTests
{
    // implement the following testcases:
    // Establish connection, expect 0 stream
    // Close connection, expect goaway
    // Try speak a protocol
    // Exchange data
    // Expect error and react to it

    [Test]
    public async Task Test_Connection_Ackowledged()
    {
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelFactory.SubProtocols.Returns(new[] { proto1 });
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(upChannel);

        YamuxProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        await downChannel.WriteLineAsync(proto.Id);
        await downChannel.WriteLineAsync("proto1");

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        await downChannel.CloseAsync();
    }

    [Test]
    public async Task Test_ConnectionClosed_ForBrokenHandshake()
    {
        // IChannel downChannel = new TestChannel();
        // IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        // IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        // IPeerContext peerContext = Substitute.For<IPeerContext>();
        //
        // IProtocol? proto1 = Substitute.For<IProtocol>();
        // proto1.Id.Returns("proto1");
        // channelFactory.SubProtocols.Returns(new[] { proto1 });
        // IChannel upChannel = new TestChannel();
        // channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
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

    class PingPongProtocol : IProtocol
    {
        public string Id => throw new NotImplementedException();

        public async Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
        {
            const string line = "hello";
            await downChannel.WriteLineAsync(line);
            await downChannel.WriteEofAsync();
            string received = await downChannel.ReadLineAsync();
            Assert.That(received, Is.EqualTo(line));
        }

        public async Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
        {
            string line = await downChannel.ReadLineAsync();
            ReadOnlySequence<byte>? readAfter = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny);
            Assert.That(readAfter, Is.Null);
            await downChannel.WriteLineAsync(line);
        }
    }
}
