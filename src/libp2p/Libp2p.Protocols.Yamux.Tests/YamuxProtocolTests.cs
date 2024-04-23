// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework.Internal;
using System.Buffers;
using System.Collections.Concurrent;

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
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

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
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        channelFactory.SubProtocols.Returns(new[] { proto1 });
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

        NoiseProtocol proto = new();
        _ = proto.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);
        await downChannel.WriteLineAsync(proto.Id);
        await downChannel.WriteLineAsync("proto2");

        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        channelFactory.DidNotReceive().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
    }

    [Test]
    public async Task Test_Protocol_Communication()
    {
        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");
        IPeerContext dialerPeerContext = Substitute.For<IPeerContext>();
        var dialerRequests = new BlockingCollection<IChannelRequest>() { new ChannelRequest() { SubProtocol = proto1 } };
        dialerPeerContext.SubDialRequests.Returns(dialerRequests);

        TestChannel dialerDownChannel = new TestChannel();
        IChannelFactory dialerUpchannelFactory = Substitute.For<IChannelFactory>();
        dialerUpchannelFactory.SubProtocols.Returns(new[] { proto1 });
        TestChannel dialerUpChannel = new TestChannel();
        dialerUpchannelFactory.SubDial(Arg.Any<IPeerContext>(), Arg.Any<IChannelRequest>())
           .Returns(dialerUpChannel);


        _ = dialerUpChannel.Reverse().WriteLineAsync("hello").AsTask().ContinueWith((e) => dialerUpChannel.CloseAsync());

        IPeerContext listenerPeerContext = Substitute.For<IPeerContext>();
        IChannel listenerDownChannel = dialerDownChannel.Reverse();
        IChannelFactory listenerUpchannelFactory = Substitute.For<IChannelFactory>();
        var listenerRequests = new BlockingCollection<IChannelRequest>();
        listenerPeerContext.SubDialRequests.Returns(listenerRequests);
        listenerUpchannelFactory.SubProtocols.Returns(new[] { proto1 });
        TestChannel listenerUpChannel = new TestChannel();
        listenerUpchannelFactory.SubListen(Arg.Any<IPeerContext>(), Arg.Any<IChannelRequest>())
           .Returns(listenerUpChannel);

        YamuxProtocol proto = new(loggerFactory: new DebugLoggerFactory());

        _ = proto.ListenAsync(listenerDownChannel, listenerUpchannelFactory, listenerPeerContext);

        _ = proto.DialAsync(dialerDownChannel, dialerUpchannelFactory, dialerPeerContext);


        var res = await listenerUpChannel.Reverse().ReadLineAsync();
        await listenerUpChannel.CloseAsync();

        Assert.That(res, Is.EqualTo("hello"));

        await Task.Delay(1000);
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
            ReadOnlySequence<byte>? readAfter = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
            Assert.That(readAfter, Is.Null);
            await downChannel.WriteLineAsync(line);
        }
    }
}
