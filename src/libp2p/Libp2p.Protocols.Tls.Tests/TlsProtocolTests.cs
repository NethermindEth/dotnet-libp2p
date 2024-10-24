// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
        IPeerContext peerContext = Substitute.For<IPeerContext>();
        IPeerContext listenerContext = Substitute.For<IPeerContext>();
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();

        TestChannel upChannel = new TestChannel();
        channelFactory.SubDial(Arg.Any<IPeerContext>(), Arg.Any<IChannelRequest>())
            .Returns(upChannel);

        TestChannel listenerUpChannel = new TestChannel();
        channelFactory.SubListen(Arg.Any<IPeerContext>(), Arg.Any<IChannelRequest>())
            .Returns(listenerUpChannel);

        peerContext.LocalPeer.Identity.Returns(new Identity());
        listenerContext.LocalPeer.Identity.Returns(new Identity());

        string peerId = peerContext.LocalPeer.Identity.PeerId.ToString();
        Multiaddress localAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{peerId}";
        peerContext.LocalPeer.Address.Returns(localAddr);
        listenerContext.RemotePeer.Address.Returns(localAddr);

        string listenerPeerId = listenerContext.LocalPeer.Identity.PeerId.ToString();
        Multiaddress listenerAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{listenerPeerId}";
        peerContext.RemotePeer.Address.Returns(listenerAddr);
        listenerContext.LocalPeer.Address.Returns(listenerAddr);

        var i_multiplexerSettings = new MultiplexerSettings();
        var r_multiplexerSettings = new MultiplexerSettings();
        TlsProtocol tlsProtocolListener = new TlsProtocol(i_multiplexerSettings, loggerFactory);
        TlsProtocol tlsProtocolInitiator = new TlsProtocol(r_multiplexerSettings, loggerFactory);

        // Act
        Task listenTask = tlsProtocolListener.ListenAsync(downChannel, channelFactory, listenerContext);
        Task dialTask = tlsProtocolInitiator.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);

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
