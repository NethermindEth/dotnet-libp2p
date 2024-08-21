// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Noise.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
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
        // await downChannel.WriteLineAsync("proto1");
        //
        // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo(proto.Id));
        // Assert.That(await downChannel.ReadLineAsync(), Is.EqualTo("proto1"));
        // channelFactory.Received().SubDialAndBind(downChannelFromProtocolPov, peerContext, proto1);
        // await downChannel.CloseAsync();
    }
   [Test]
    public async Task Test_ConnectionEstablished_With_PreSelectedMuxer()
    {
        // Arrange
        IChannel downChannel = new TestChannel();
        IChannel downChannelFromProtocolPov = ((TestChannel)downChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();
        IPeerContext listenerContext = Substitute.For<IPeerContext>();

        IProtocol? proto1 = Substitute.For<IProtocol>();
        proto1.Id.Returns("proto1");

        IProtocol? proto2 = Substitute.For<IProtocol>();
        proto2.Id.Returns("proto2");

        channelFactory.SubProtocols.Returns(new[] { proto1, proto2 });

        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(Task.FromResult(upChannel));
        channelFactory.SubListenAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);

        var i_multiplexerSettings = new MultiplexerSettings();
        var r_multiplexerSettings = new MultiplexerSettings();
        r_multiplexerSettings.Add(proto2);
        r_multiplexerSettings.Add(proto1);
        i_multiplexerSettings.Add(proto1);

        NoiseProtocol proto_initiator = new(i_multiplexerSettings);
        NoiseProtocol proto_responder = new(r_multiplexerSettings);

        peerContext.LocalPeer.Identity.Returns(new Identity());
        listenerContext.LocalPeer.Identity.Returns(new Identity());
        string peerId = peerContext.LocalPeer.Identity.PeerId.ToString();
        Multiaddress localAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{peerId}";
        peerContext.RemotePeer.Address.Returns(localAddr);

        string listenerPeerId = listenerContext.LocalPeer.Identity.PeerId.ToString();
        Multiaddress listenerAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{listenerPeerId}";
        listenerContext.RemotePeer.Address.Returns(listenerAddr);

        // Act
        Task listenTask = proto_responder.ListenAsync(downChannel, channelFactory, listenerContext);
        Task dialTask = proto_initiator.DialAsync(downChannelFromProtocolPov, channelFactory, peerContext);

        await Task.Delay(TimeSpan.FromSeconds(2));

        // Assert
        Assert.That(peerContext.SpecificProtocolRequest.SubProtocol, Is.EqualTo(proto1));

        // Cleanup
        await downChannel.CloseAsync();
        await upChannel.CloseAsync();
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
}
