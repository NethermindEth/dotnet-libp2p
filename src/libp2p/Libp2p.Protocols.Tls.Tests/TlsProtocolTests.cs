// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TlsProtocolTests
{
    [Test]
    [Ignore("Infinite loop")]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        // Arrange
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IConnectionContext listenerContext = Substitute.For<IConnectionContext>();
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();

        TestChannel upChannel = new();
        channelFactory.Upgrade(Arg.Any<UpgradeOptions>()).Returns(upChannel);

        TestChannel listenerUpChannel = new();
        channelFactory.Upgrade(Arg.Any<UpgradeOptions>()).Returns(listenerUpChannel);

        IConnectionContext dialerContext = Substitute.For<IConnectionContext>();
        dialerContext.Peer.Identity.Returns(TestPeers.Identity(1));
        dialerContext.Peer.ListenAddresses.Returns([TestPeers.Multiaddr(1)]);
        dialerContext.State.Returns(new State());


        listenerContext.Peer.Identity.Returns(TestPeers.Identity(2));

        string peerId = dialerContext.Peer.Identity.PeerId.ToString();
        Multiaddress localAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{peerId}";
        //listenerContext.State.RemoteAddress.Returns(localAddr);

        string listenerPeerId = listenerContext.Peer.Identity.PeerId.ToString();
        Multiaddress listenerAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{listenerPeerId}";
        //dialerContext.State.RemoteAddress.Returns(listenerAddr);
        //listenerContext.State.RemoteAddress.Returns(listenerAddr);

        MultiplexerSettings i_multiplexerSettings = new();
        MultiplexerSettings r_multiplexerSettings = new();
        TlsProtocol tlsProtocolListener = new(i_multiplexerSettings, loggerFactory);
        TlsProtocol tlsProtocolInitiator = new(r_multiplexerSettings, loggerFactory);

        // Act
        Task listenTask = tlsProtocolListener.ListenAsync(downChannel, listenerContext);
        Task dialTask = tlsProtocolInitiator.DialAsync(downChannelFromProtocolPov, dialerContext);

        int sent = 42;
        ValueTask<IOResult> writeTask = listenerUpChannel.Reverse().WriteVarintAsync(sent);
        int received = await upChannel.Reverse().ReadVarintAsync();
        await writeTask;

        await upChannel.CloseAsync();
        await listenerUpChannel.CloseAsync();
        await downChannel.CloseAsync();

        // Assert
        Assert.That(received, Is.EqualTo(sent));
    }
}
