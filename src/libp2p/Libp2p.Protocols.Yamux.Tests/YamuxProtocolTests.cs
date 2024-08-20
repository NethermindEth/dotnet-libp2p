// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework.Internal;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

// TODO: Add tests
[TestFixture]
public class YamuxProtocolTests
{
    // TODO:
    // Implement the following test cases:
    // Establish connection, expect 0 stream
    // Close connection, expect goaway
    // Try speak a protocol
    // Exchange data
    // Expect error and react to it

    [Test]
    public async Task Test_Protocol_Communication()
    {
        //IProtocol? proto1 = Substitute.For<IProtocol>();
        //proto1.Id.Returns("proto1");
        //IPeerContext dialerPeerContext = Substitute.For<IPeerContext>();
        //var dialerRequests = new BlockingCollection<IChannelRequest>() { new ChannelRequest() { SubProtocol = proto1 } };
        //dialerPeerContext.SubDialRequests.Returns(dialerRequests);

        //TestChannel dialerDownChannel = new TestChannel();
        //IChannelFactory dialerUpchannelFactory = Substitute.For<IChannelFactory>();
        //dialerUpchannelFactory.SubProtocols.Returns(new[] { proto1 });
        //TestChannel dialerUpChannel = new TestChannel();
        //dialerUpchannelFactory.SubDial(Arg.Any<IChannelRequest>())
        //   .Returns(dialerUpChannel);


        //_ = dialerUpChannel.Reverse().WriteLineAsync("hello").AsTask().ContinueWith((e) => dialerUpChannel.CloseAsync());

        //IPeerContext listenerPeerContext = Substitute.For<IPeerContext>();
        //IChannel listenerDownChannel = dialerDownChannel.Reverse();
        //IChannelFactory listenerUpchannelFactory = Substitute.For<IChannelFactory>();
        //var listenerRequests = new BlockingCollection<IChannelRequest>();
        //listenerPeerContext.SubDialRequests.Returns(listenerRequests);
        //listenerUpchannelFactory.SubProtocols.Returns(new[] { proto1 });
        //TestChannel listenerUpChannel = new TestChannel();
        //listenerUpchannelFactory.SubListen(Arg.Any<IChannelRequest>())
        //   .Returns(listenerUpChannel);

        //YamuxProtocol proto = new(loggerFactory: new DebugLoggerFactory());

        //_ = proto.ListenAsync(listenerDownChannel, listenerUpchannelFactory, listenerPeerContext);

        //_ = proto.DialAsync(dialerDownChannel, dialerUpchannelFactory, dialerPeerContext);


        //var res = await listenerUpChannel.Reverse().ReadLineAsync();
        //await listenerUpChannel.CloseAsync();

        //Assert.That(res, Is.EqualTo("hello"));

        //await Task.Delay(1000);
    }
}
