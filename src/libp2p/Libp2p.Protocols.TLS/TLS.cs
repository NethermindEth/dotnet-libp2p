using System.Buffers;
using System.Net;
using System.Net.Security;
using Nethermind.Libp2p.Protocols.Quic;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols;

public class TlsProtocol : IProtocol
{
    private readonly ILogger<TlsProtocol>? _logger;
    private readonly ECDsa _sessionKey;
    public SslApplicationProtocol? LastNegotiatedApplicationProtocol { get; private set; }
    private readonly List<SslApplicationProtocol> _protocols;

    public TlsProtocol(List<SslApplicationProtocol> protocols, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<TlsProtocol>();
        _sessionKey = ECDsa.Create();
        _protocols = protocols;
    }

    public string Id => "tls-tcp";

    public async Task ListenAsync(IChannel signalingChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogDebug("Handling connection");
        if (channelFactory is null)
        {
            throw new ArgumentException("Protocol is not properly instantiated");
        }

        Multiaddress addr = context.LocalPeer.Address;
        bool isIP4 = addr.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int tcpPort = int.Parse(addr.Get<TCP>().ToString());

        TcpListener listener = new(ipAddress, tcpPort);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Failed to start listener: {Message}", ex.Message);
            return;
        }

        IPEndPoint localIpEndpoint = (IPEndPoint)listener.LocalEndpoint!;

        Multiaddress localMultiaddress = new();
        localMultiaddress = isIP4 ? localMultiaddress.Add<IP4>(localIpEndpoint.Address.MapToIPv4()) : localMultiaddress.Add<IP6>(localIpEndpoint.Address.MapToIPv6());
        localMultiaddress = localMultiaddress.Add<TCP>(localIpEndpoint.Port);
        context.LocalEndpoint = localMultiaddress;

        if (tcpPort == 0)
        {
            context.LocalPeer.Address = context.LocalPeer.Address.ReplaceOrAdd<TCP>(localIpEndpoint.Port);
        }

        _logger?.LogDebug("TLS server ready to handle connections");
        context.ListenerReady();

        TaskAwaiter signalingWaiter = signalingChannel.GetAwaiter();
        signalingWaiter.OnCompleted(() =>
        {
            listener.Stop();
        });

        while (true)
        {
            try
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync();
                IPEndPoint remoteIpEndpoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint!;

                bool isRemoteIP4 = remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork;

                Multiaddress remoteMultiaddress = new();
                string strRemoteEndpointAddress = remoteIpEndpoint.Address.ToString();
                remoteMultiaddress = isRemoteIP4 ? remoteMultiaddress.Add<IP4>(strRemoteEndpointAddress) : remoteMultiaddress.Add<IP6>(strRemoteEndpointAddress);
                remoteMultiaddress = remoteMultiaddress.Add<TCP>(remoteIpEndpoint.Port);

                context.RemoteEndpoint = remoteMultiaddress;
                context.Connected(context.RemotePeer);

                X509Certificate certificate = CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity);

                SslServerAuthenticationOptions serverAuthenticationOptions = new()
                {
                    ApplicationProtocols = _protocols,
                    RemoteCertificateValidationCallback = (_, c, _, _) => VerifyRemoteCertificate(context.RemotePeer.Identity.PeerId.ToString(), c),
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                };

                _logger?.LogDebug("Accepted new TCP client connection from {RemoteEndPoint}", tcpClient.Client.RemoteEndPoint);
                SslStream sslStream = new(tcpClient.GetStream(), false, serverAuthenticationOptions.RemoteCertificateValidationCallback);

                try
                {
                    await sslStream.AuthenticateAsServerAsync(serverAuthenticationOptions);
                }
                catch (Exception ex)
                {
                    _logger?.LogError("An error occurred during TLS authentication: {Message}", ex.Message);
                    _logger?.LogDebug("Exception details: {StackTrace}", ex.StackTrace);
                    throw;
                }

                if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
                {
                    _logger?.LogDebug("HTTP/2 protocol negotiated");
                }
                else if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http11)
                {
                    _logger?.LogDebug("HTTP/1.1 protocol negotiated");
                }
                else if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http3)
                {
                    _logger?.LogDebug("HTTP/3 protocol negotiated");
                }
                else
                {
                    _logger?.LogDebug("Unknown protocol negotiated");
                }

                IChannel upChannel = channelFactory.SubListen(context);
                await ProcessStreamsAsync(sslStream, tcpClient, upChannel, context);
            }
            catch (Exception ex)
            {
                _logger?.LogError("An unexpected exception occurred while accepting TCP client: {Message}", ex.Message);
                _logger?.LogDebug("Exception details: {StackTrace}", ex.StackTrace);
                break;
            }
        }
    }

    public async Task DialAsync(IChannel signalingChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("Handling connection");
        if (channelFactory is null)
        {
            throw new ArgumentException("Protocol is not properly instantiated");
        }

        Multiaddress addr = context.RemotePeer.Address;
        bool rIsIp4 = addr.Has<IP4>();
        MultiaddressProtocol protocol = rIsIp4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress rIpAddress = IPAddress.Parse(protocol.ToString()!);
        int tcpPort = int.Parse(addr.Get<TCP>().ToString()!);
        IPEndPoint remoteEndpoint = new(rIpAddress, tcpPort);
        TcpClient tcpClient = new();

        try
        {
            await tcpClient.ConnectAsync(rIpAddress, tcpPort);
        }
        catch (SocketException ex)
        {
            _logger?.LogError("Failed to connect: {Message}", ex.Message);
            return;
        }

        IPEndPoint localEndpoint = (IPEndPoint)tcpClient.Client.LocalEndPoint!;
        IPEndPoint remoteIpEndpoint = (IPEndPoint)tcpClient.Client.RemoteEndPoint!;

        bool isIP4 = remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress remoteMultiaddress = new();
        var remoteIpAddress = isIP4 ? remoteIpEndpoint.Address.MapToIPv4() : remoteIpEndpoint.Address.MapToIPv6();
        remoteMultiaddress = isIP4 ? remoteMultiaddress.Add<IP4>(remoteIpAddress) : remoteMultiaddress.Add<IP6>(remoteIpAddress);
        context.RemoteEndpoint = remoteMultiaddress.Add<TCP>(remoteIpEndpoint.Port);

        Multiaddress localMultiaddress = new();
        var localIpAddress = isIP4 ? localEndpoint.Address.MapToIPv4() : localEndpoint.Address.MapToIPv6();
        localMultiaddress = isIP4 ? localMultiaddress.Add<IP4>(localIpAddress) : localMultiaddress.Add<IP6>(localIpAddress);
        context.LocalEndpoint = localMultiaddress.Add<TCP>(localEndpoint.Port);

        context.LocalPeer.Address = context.LocalEndpoint.Add<P2P>(context.LocalPeer.Identity.PeerId.ToString());
        context.Connected(context.RemotePeer);

        SslClientAuthenticationOptions clientAuthenticationOptions = new()
        {
            TargetHost = context.RemoteEndpoint.ToString(),
            ApplicationProtocols = _protocols,
            RemoteCertificateValidationCallback = (_, c, _, _) => VerifyRemoteCertificate(context.RemotePeer.Identity.PeerId.ToString(), c),
            ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity) },
        };

        SslStream sslStream = new(tcpClient.GetStream(), false, clientAuthenticationOptions.RemoteCertificateValidationCallback);

        try
        {
            await sslStream.AuthenticateAsClientAsync(clientAuthenticationOptions);

            if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
            {
                _logger?.LogDebug("HTTP/2 protocol negotiated");
            }
            else if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http11)
            {
                _logger?.LogDebug("HTTP/1.1 protocol negotiated");
            }
            else if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http3)
            {
                _logger?.LogDebug("HTTP/3 protocol negotiated");
            }
            else
            {
                _logger?.LogDebug("Unknown protocol negotiated");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError("An error occurred while authenticating the server: {Message}", ex.Message);
            return;
        }

        LastNegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;

        IChannel upChannel = channelFactory.SubDial(context);
        await ProcessStreamsAsync(sslStream, tcpClient, upChannel, context);

        signalingChannel.GetAwaiter().OnCompleted(() =>
        {
            tcpClient.Close();
        });
    }

    private static bool VerifyRemoteCertificate(string? remotePeerId, X509Certificate? certificate)
    {
        if (certificate == null)
        {
            throw new ArgumentNullException(nameof(certificate), "Certificate cannot be null.");
        }

        return CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeerId);
    }

    private static Task ProcessStreamsAsync(SslStream sslStream, TcpClient tcpClient, IChannel upChannel, IPeerContext context, CancellationToken cancellationToken = default)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            sslStream.Close();
        });

        Task t = Task.Run(async () =>
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (tcpClient.Connected)
                {
                    int bytesRead = await sslStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0)
                        break;

                    if (await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(0, bytesRead)), cancellationToken) != IOResult.Ok)
                        await upChannel.WriteEofAsync();
                }
            }
            catch (Exception)
            {
                await upChannel.CloseAsync();
            }
        }, cancellationToken);

        Task t2 = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync(cancellationToken))
                {
                    await sslStream.WriteAsync(data.ToArray(), cancellationToken);
                }
            }
            catch (Exception)
            {
                await upChannel.CloseAsync();
            }
        }, cancellationToken);

        sslStream.Dispose();
        tcpClient.Close();

        return Task.WhenAny(t, t2).ContinueWith(_ => { });
    }
}
