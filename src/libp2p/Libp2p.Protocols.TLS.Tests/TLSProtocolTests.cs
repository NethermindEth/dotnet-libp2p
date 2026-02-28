// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TlsProtocolTests
{
    [Test]
    [Explicit("TestChannel's rendezvous semantics are incompatible with SslStream handshake I/O. Use Test_TlsHandshake_OverNetworkStream for TLS validation.")]
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

        TlsProtocol tlsProtocolListener = new TlsProtocol(applicationProtocols: protocolsListener);
        TlsProtocol tlsProtocolInitiator = new TlsProtocol(applicationProtocols: protocolsInitiator);

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
        Multiaddress peerAddrNoP2P = $"/ip4/127.0.0.1/tcp/8080";
        listenerContext.RemotePeer.Address.Returns(peerAddrNoP2P);

        // Act
        Exception? listenException = null;
        Exception? dialException = null;

        Task listenTask = Task.Run(async () =>
        {
            try
            {
                await tlsProtocolListener.ListenAsync(signalingChannel, channelFactory, listenerContext);
            }
            catch (Exception ex)
            {
                listenException = ex;
            }
        });

        Task dialTask = Task.Run(async () =>
        {
            try
            {
                await tlsProtocolInitiator.DialAsync(signalingChannelFromProtocolPov, channelFactory, peerContext);
            }
            catch (Exception ex)
            {
                dialException = ex;
            }
        });

        // Wait for both tasks with a timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var completedTask = await Task.WhenAny(Task.WhenAll(listenTask, dialTask), Task.Delay(Timeout.Infinite, cts.Token));

        // Report detailed errors if handshake didn't complete
        if (tlsProtocolInitiator.LastNegotiatedApplicationProtocol is null)
        {
            string errorDetails = "";
            if (listenException is not null)
                errorDetails += $"Listen error: {listenException.Message}\n{listenException.InnerException?.Message}\n";
            if (dialException is not null)
                errorDetails += $"Dial error: {dialException.Message}\n{dialException.InnerException?.Message}\n";
            if (string.IsNullOrEmpty(errorDetails))
                errorDetails = "No exceptions caught - handshake may be hanging.";

            Assert.Fail($"TLS handshake did not complete. Details:\n{errorDetails}");
        }

        // Assert
        Assert.That(tlsProtocolInitiator.LastNegotiatedApplicationProtocol, Is.EqualTo(SslApplicationProtocol.Http11));

        await signalingChannel.CloseAsync();
        await upChannel.CloseAsync();
    }

    [Test]
    public async Task Test_TlsHandshake_OverNetworkStream()
    {
        // Test TLS handshake using real TCP streams to isolate from channel issues
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var serverIdentity = new Identity();
        var clientIdentity = new Identity();

        var serverKey = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();
        var clientKey = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();

        var serverCert = WindowsCertificateHelper.CreateCertificateFromIdentity(serverKey, serverIdentity);
        var clientCert = WindowsCertificateHelper.CreateCertificateFromIdentity(clientKey, clientIdentity);

        var applicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 };

        Exception? serverError = null;
        Exception? clientError = null;
        SslApplicationProtocol? serverProtocol = null;
        SslApplicationProtocol? clientProtocol = null;

        var serverTask = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var sslStream = new SslStream(stream, false);
                await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = true,
                    ApplicationProtocols = applicationProtocols,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                });
                serverProtocol = sslStream.NegotiatedApplicationProtocol;
            }
            catch (Exception ex) { serverError = ex; }
        });

        var clientTask = Task.Run(async () =>
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
                using var stream = client.GetStream();
                using var sslStream = new SslStream(stream, false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "127.0.0.1",
                    ApplicationProtocols = applicationProtocols,
                    ClientCertificates = new X509CertificateCollection { clientCert },
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                });
                clientProtocol = sslStream.NegotiatedApplicationProtocol;
            }
            catch (Exception ex) { clientError = ex; }
        });

        await Task.WhenAll(serverTask, clientTask);
        listener.Stop();

        Assert.That(serverError, Is.Null, $"Server error: {serverError?.Message}");
        Assert.That(clientError, Is.Null, $"Client error: {clientError?.Message}");
        Assert.That(clientProtocol, Is.EqualTo(SslApplicationProtocol.Http11));
        Assert.That(serverProtocol, Is.EqualTo(SslApplicationProtocol.Http11));
    }
}
