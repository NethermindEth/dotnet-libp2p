// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework.Internal;

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
    public async Task Test_Protocol_Communication2()
    {
        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        INewSessionContext dialerSessionContext = Substitute.For<INewSessionContext>();
        dialerContext.UpgradeToSession().Returns(dialerSessionContext);
        dialerContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        dialerSessionContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        dialerSessionContext.Id.Returns("dialer");

        dialerSessionContext.DialRequests.Returns([new UpgradeOptions() { SelectedProtocol = proto1 }]);

        TestChannel dialerDownChannel = new();
        dialerSessionContext.SubProtocols.Returns([proto1]);
        TestChannel dialerUpChannel = new();
        dialerSessionContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(dialerUpChannel);

        _ = dialerUpChannel.Reverse().WriteLineAsync("hello").AsTask().ContinueWith((e) => dialerUpChannel.CloseAsync());

        IChannel listenerDownChannel = dialerDownChannel.Reverse();

        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        INewSessionContext listenerSessionContext = Substitute.For<INewSessionContext>();
        listenerContext.UpgradeToSession().Returns(listenerSessionContext);
        listenerContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        listenerSessionContext.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        listenerSessionContext.Id.Returns("listener");

        listenerSessionContext.SubProtocols.Returns([proto1]);
        TestChannel listenerUpChannel = new();
        listenerSessionContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        YamuxProtocol proto = new(loggerFactory: new TestContextLoggerFactory());

        _ = proto.ListenAsync(listenerDownChannel, listenerContext);

        _ = proto.DialAsync(dialerDownChannel, dialerContext);


        string res = await listenerUpChannel.Reverse().ReadLineAsync();
        await listenerUpChannel.CloseAsync();

        Assert.That(res, Is.EqualTo("hello"));
    }
}
