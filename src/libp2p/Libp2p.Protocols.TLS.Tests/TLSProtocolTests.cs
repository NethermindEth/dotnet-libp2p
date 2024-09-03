// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TlsProtocolTests
{
    [Test]
    public async Task Test_ConnectionEstablished_AfterHandshake()
    {
        // Arrange
        IChannel signalingChannel = new TestChannel();
        IChannel signalingChannelFromProtocolPov = ((TestChannel)signalingChannel).Reverse();
        IChannelFactory channelFactory = Substitute.For<IChannelFactory>();
        IPeerContext peerContext = Substitute.For<IPeerContext>();
        IPeerContext listenerContext = Substitute.For<IPeerContext>();
        IChannel upChannel = new TestChannel();
        channelFactory.SubDialAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(Task.FromResult(upChannel));
        channelFactory.SubListenAndBind(Arg.Any<IChannel>(), Arg.Any<IPeerContext>(), Arg.Any<IProtocol>())
            .Returns(Task.CompletedTask);
        IPeer peerLocalPeer = Substitute.For<IPeer>();
        IPeer listenerLocalPeer = Substitute.For<IPeer>();

        Identity peerIdentity = new Identity();
        Identity listenerIdentity = new Identity();

        peerLocalPeer.Identity.Returns(peerIdentity);
        listenerLocalPeer.Identity.Returns(listenerIdentity);

        List<SslApplicationProtocol> protocolsListener = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11, SslApplicationProtocol.Http2 };
        List<SslApplicationProtocol> protocolsInitiator = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11, SslApplicationProtocol.Http3 };

        TlsProtocol tlsProtocolListener = new TlsProtocol(protocolsListener);
        TlsProtocol tlsProtocolInitiator = new TlsProtocol(protocolsInitiator);

        string peerId = peerLocalPeer.Identity.PeerId.ToString();
        Multiaddress localAddr = $"/ip4/127.0.0.1/tcp/8080/p2p/{peerId}";
        peerLocalPeer.Address.Returns(localAddr);

        string listenerPeerId = listenerLocalPeer.Identity.PeerId.ToString();
        Multiaddress listenerAddr = $"/ip4/127.0.0.2/tcp/8083/p2p/{listenerPeerId}";
        listenerLocalPeer.Address.Returns(listenerAddr);

        peerContext.LocalPeer.Returns(peerLocalPeer);
        listenerContext.LocalPeer.Returns(listenerLocalPeer);

        peerContext.RemotePeer.Identity.Returns(x => listenerLocalPeer.Identity);
        listenerContext.RemotePeer.Identity.Returns(x => peerLocalPeer.Identity);

        peerContext.RemotePeer.Address.Returns(listenerAddr);

        // Act
        Task listenTask = tlsProtocolListener.ListenAsync(signalingChannel, channelFactory, listenerContext);
        Task dialTask = tlsProtocolInitiator.DialAsync(signalingChannelFromProtocolPov, channelFactory, peerContext);

        await Task.Delay(TimeSpan.FromSeconds(10));

        // Assert
        Assert.That(tlsProtocolInitiator.LastNegotiatedApplicationProtocol, Is.EqualTo(SslApplicationProtocol.Http11));

        await signalingChannel.CloseAsync();
        await upChannel.CloseAsync();
    }
}
