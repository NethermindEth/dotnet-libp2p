// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using NSubstitute;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

[TestFixture]
[Parallelizable(scope: ParallelScope.All)]
public class TlsProtocolTests
{
    [Test]
    [Explicit("TestChannel-based test needs infrastructure fix")]
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

        string listenerPeerId = listenerContext.Peer.Identity.PeerId.ToString();
        Multiaddress listenerAddr = $"/ip4/0.0.0.0/tcp/0/p2p/{listenerPeerId}";

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

    [Test]
    public void Test_CertificateFromIdentity_CreatesValidCertificate()
    {
        // Arrange
        Identity identity = TestPeers.Identity(1);
        ECDsa sessionKey = ECDsa.Create();

        // Act
        X509Certificate2 cert = CertificateHelper.CertificateFromIdentity(sessionKey, identity);

        // Assert
        Assert.That(cert, Is.Not.Null);
        Assert.That(cert.HasPrivateKey, Is.True);
        Assert.That(cert.Subject, Does.Contain("SERIALNUMBER="));
    }

    [Test]
    public void Test_CertificateValidation_Succeeds()
    {
        // Arrange
        Identity identity = TestPeers.Identity(1);
        ECDsa sessionKey = ECDsa.Create();
        X509Certificate2 cert = CertificateHelper.CertificateFromIdentity(sessionKey, identity);

        // Act
        bool isValid = CertificateHelper.ValidateCertificate(cert, identity.PeerId.ToString());

        // Assert
        Assert.That(isValid, Is.True);
    }

    [Test]
    public void Test_CertificateValidation_FailsForWrongPeerId()
    {
        // Arrange
        Identity identity1 = TestPeers.Identity(1);
        Identity identity2 = TestPeers.Identity(2);
        ECDsa sessionKey = ECDsa.Create();
        X509Certificate2 cert = CertificateHelper.CertificateFromIdentity(sessionKey, identity1);

        // Act
        bool isValid = CertificateHelper.ValidateCertificate(cert, identity2.PeerId.ToString());

        // Assert
        Assert.That(isValid, Is.False);
    }

    [Test]
    public void Test_TlsProtocol_HasCorrectId()
    {
        // Arrange & Act
        TlsProtocol protocol = new();

        // Assert
        Assert.That(protocol.Id, Is.EqualTo("/tls/1.0.0"));
    }

    [Test]
    public async Task Test_TlsHandshake_OverNetworkStream()
    {
        // Arrange - create two identities
        Identity serverIdentity = TestPeers.Identity(10);
        Identity clientIdentity = TestPeers.Identity(11);

        ECDsa serverSessionKey = ECDsa.Create();
        ECDsa clientSessionKey = ECDsa.Create();

        X509Certificate2 serverCert = CertificateHelper.CertificateFromIdentity(serverSessionKey, serverIdentity);
        X509Certificate2 clientCert = CertificateHelper.CertificateFromIdentity(clientSessionKey, clientIdentity);

        // Set up TCP listener
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string testMessage = "Hello from TLS client!";
        string receivedMessage = "";
        Exception? serverException = null;
        Exception? clientException = null;

        // Server task
        Task serverTask = Task.Run(async () =>
        {
            try
            {
                using TcpClient serverClient = await listener.AcceptTcpClientAsync();
                using NetworkStream networkStream = serverClient.GetStream();

                SslServerAuthenticationOptions serverOptions = new()
                {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        CertificateHelper.ValidateCertificate(cert as X509Certificate2, null),
                };

                using SslStream sslStream = new(networkStream, false, serverOptions.RemoteCertificateValidationCallback);
                await sslStream.AuthenticateAsServerAsync(serverOptions);

                byte[] buffer = new byte[1024];
                int bytesRead = await sslStream.ReadAsync(buffer);
                receivedMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // Echo back
                await sslStream.WriteAsync(Encoding.UTF8.GetBytes(receivedMessage));
                await sslStream.FlushAsync();
            }
            catch (Exception ex)
            {
                serverException = ex;
            }
        });

        // Client task
        Task clientTask = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = new();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using NetworkStream networkStream = client.GetStream();

                SslClientAuthenticationOptions clientOptions = new()
                {
                    TargetHost = "127.0.0.1",
                    ClientCertificates = [clientCert],
                    EnabledSslProtocols = SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        CertificateHelper.ValidateCertificate(cert as X509Certificate2, null),
                    CertificateChainPolicy = new X509ChainPolicy
                    {
                        RevocationMode = X509RevocationMode.NoCheck,
                        VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority,
                    },
                };

                using SslStream sslStream = new(networkStream, false, clientOptions.RemoteCertificateValidationCallback);
                await sslStream.AuthenticateAsClientAsync(clientOptions);

                await sslStream.WriteAsync(Encoding.UTF8.GetBytes(testMessage));
                await sslStream.FlushAsync();

                byte[] buffer = new byte[1024];
                int bytesRead = await sslStream.ReadAsync(buffer);
                string echoedMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Assert.That(echoedMessage, Is.EqualTo(testMessage));
            }
            catch (Exception ex)
            {
                clientException = ex;
            }
        });

        // Act
        await Task.WhenAll(serverTask, clientTask).WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();

        // Assert
        if (serverException is not null) throw serverException;
        if (clientException is not null) throw clientException;
        Assert.That(receivedMessage, Is.EqualTo(testMessage));
    }

    [Test]
    public async Task Test_TlsHandshake_BidirectionalData()
    {
        // Arrange
        Identity serverIdentity = TestPeers.Identity(20);
        Identity clientIdentity = TestPeers.Identity(21);

        ECDsa serverSessionKey = ECDsa.Create();
        ECDsa clientSessionKey = ECDsa.Create();

        X509Certificate2 serverCert = CertificateHelper.CertificateFromIdentity(serverSessionKey, serverIdentity);
        X509Certificate2 clientCert = CertificateHelper.CertificateFromIdentity(clientSessionKey, clientIdentity);

        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string serverMessage = "Hello from server!";
        string clientMessage = "Hello from client!";
        string serverReceived = "";
        string clientReceived = "";

        Task serverTask = Task.Run(async () =>
        {
            using TcpClient serverClient = await listener.AcceptTcpClientAsync();
            using NetworkStream networkStream = serverClient.GetStream();

            SslServerAuthenticationOptions opts = new()
            {
                ServerCertificate = serverCert,
                ClientCertificateRequired = true,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    CertificateHelper.ValidateCertificate(cert as X509Certificate2, null),
            };

            using SslStream sslStream = new(networkStream, false, opts.RemoteCertificateValidationCallback);
            await sslStream.AuthenticateAsServerAsync(opts);

            // Send server message
            await sslStream.WriteAsync(Encoding.UTF8.GetBytes(serverMessage));
            await sslStream.FlushAsync();

            // Read client message
            byte[] buffer = new byte[1024];
            int bytesRead = await sslStream.ReadAsync(buffer);
            serverReceived = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        });

        Task clientTask = Task.Run(async () =>
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port);
            using NetworkStream networkStream = client.GetStream();

            SslClientAuthenticationOptions opts = new()
            {
                TargetHost = "127.0.0.1",
                ClientCertificates = [clientCert],
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    CertificateHelper.ValidateCertificate(cert as X509Certificate2, null),
                CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority,
                },
            };

            using SslStream sslStream = new(networkStream, false, opts.RemoteCertificateValidationCallback);
            await sslStream.AuthenticateAsClientAsync(opts);

            // Send client message
            await sslStream.WriteAsync(Encoding.UTF8.GetBytes(clientMessage));
            await sslStream.FlushAsync();

            // Read server message
            byte[] buffer = new byte[1024];
            int bytesRead = await sslStream.ReadAsync(buffer);
            clientReceived = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        });

        // Act
        await Task.WhenAll(serverTask, clientTask).WaitAsync(TimeSpan.FromSeconds(15));
        listener.Stop();

        // Assert
        Assert.That(serverReceived, Is.EqualTo(clientMessage));
        Assert.That(clientReceived, Is.EqualTo(serverMessage));
    }
}
