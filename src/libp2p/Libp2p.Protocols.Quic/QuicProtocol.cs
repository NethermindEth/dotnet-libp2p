// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.Net.Sockets;
using MultiaddrEnum = Nethermind.Libp2p.Core.Enums.Multiaddr;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Protocols.Quic;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Protocols;

#pragma warning disable CA1416 // Do not inform about platform compatibility
#pragma warning disable CA2252 // EnablePreviewFeatures is set in the project, but build still fails
public class QuicProtocol : IProtocol
{
    private readonly ILogger? _logger;
    private readonly ECDsa _sessionKey;

    public QuicProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<QuicProtocol>();
        _sessionKey = ECDsa.Create();
    }

    private static readonly List<SslApplicationProtocol> protocols = new()
    {
        new SslApplicationProtocol("libp2p"),
        // SslApplicationProtocol.Http3, // webtransport
    };

    public string Id => "quic";

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException($"The protocol requires {nameof(channelFactory)}");
        }

        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        Multiaddr addr = context.LocalPeer.Address;
        MultiaddrEnum ipProtocol = addr.Has(MultiaddrEnum.Ip4) ? MultiaddrEnum.Ip4 : MultiaddrEnum.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int udpPort = int.Parse(addr.At(MultiaddrEnum.Udp)!);

        IPEndPoint localEndpoint = new(ipAddress, udpPort);

        QuicServerConnectionOptions serverConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.

            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, c, _, _) => VerifyRemoteCertificate(context.RemotePeer, c),
                ServerCertificate = CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity)
            },
        };

        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = localEndpoint,
            ApplicationProtocols = protocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
        });

        context.LocalEndpoint = Multiaddr.From(
            ipProtocol, listener.LocalEndPoint.Address.ToString(),
            MultiaddrEnum.Udp, listener.LocalEndPoint.Port);

        if (udpPort == 0)
        {
            context.LocalPeer.Address = context.LocalPeer.Address
                .Replace(MultiaddrEnum.Udp, listener.LocalEndPoint.Port.ToString());
        }

        channel.OnClose(async () =>
        {
            await listener.DisposeAsync();
        });

        _logger?.LogDebug("Ready to handle connections");
        context.ListenerReady();

        while (!channel.IsClosed)
        {
            QuicConnection connection = await listener.AcceptConnectionAsync(channel.Token);
            _ = ProcessStreams(connection, context.Fork(), channelFactory, channel.Token);
        }
    }

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException($"The protocol requires {nameof(channelFactory)}");
        }

        if (!QuicConnection.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        Multiaddr addr = context.LocalPeer.Address;
        MultiaddrEnum ipProtocol = addr.Has(MultiaddrEnum.Ip4) ? MultiaddrEnum.Ip4 : MultiaddrEnum.Ip6;
        IPAddress ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        int udpPort = int.Parse(addr.At(MultiaddrEnum.Udp)!);

        IPEndPoint localEndpoint = new(ipAddress, udpPort);


        addr = context.RemotePeer.Address;
        ipProtocol = addr.Has(MultiaddrEnum.Ip4) ? MultiaddrEnum.Ip4 : MultiaddrEnum.Ip6;
        ipAddress = IPAddress.Parse(addr.At(ipProtocol)!);
        udpPort = int.Parse(addr.At(MultiaddrEnum.Udp)!);

        IPEndPoint remoteEndpoint = new(ipAddress, udpPort);

        QuicClientConnectionOptions clientConnectionOptions = new()
        {
            LocalEndPoint = localEndpoint,
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.
            MaxInboundUnidirectionalStreams = 100,
            MaxInboundBidirectionalStreams = 100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = null,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, c, _, _) => VerifyRemoteCertificate(context.RemotePeer, c),
                ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity) },
            },
            RemoteEndPoint = remoteEndpoint,
        };

        QuicConnection connection = await QuicConnection.ConnectAsync(clientConnectionOptions);

        channel.OnClose(async () =>
        {
            await connection.CloseAsync(0);
            await connection.DisposeAsync();
        });

        _logger?.LogDebug($"Connected {connection.LocalEndPoint} --> {connection.RemoteEndPoint}");

        await ProcessStreams(connection, context, channelFactory, channel.Token);
    }

    private static bool VerifyRemoteCertificate(IPeer? remotePeer, X509Certificate certificate) =>
         CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeer?.Address.At(MultiaddrEnum.P2p));

    private async Task ProcessStreams(QuicConnection connection, IPeerContext context, IChannelFactory channelFactory, CancellationToken token)
    {
        MultiaddrEnum newIpProtocol = connection.LocalEndPoint.AddressFamily == AddressFamily.InterNetwork
           ? MultiaddrEnum.Ip4
           : MultiaddrEnum.Ip6;

        context.LocalEndpoint = Multiaddr.From(
            newIpProtocol,
            connection.LocalEndPoint.Address.ToString(),
            MultiaddrEnum.Udp,
            connection.LocalEndPoint.Port);

        context.LocalPeer.Address = context.LocalPeer.Address.Replace(
                context.LocalEndpoint.Has(MultiaddrEnum.Ip4) ?
                    MultiaddrEnum.Ip4 :
                    MultiaddrEnum.Ip6,
                newIpProtocol,
                connection.LocalEndPoint.Address.ToString());

        IPEndPoint remoteIpEndpoint = connection.RemoteEndPoint!;
        newIpProtocol = remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork
           ? MultiaddrEnum.Ip4
           : MultiaddrEnum.Ip6;

        context.RemoteEndpoint = Multiaddr.From(
            newIpProtocol,
            remoteIpEndpoint.Address.ToString(),
            MultiaddrEnum.Udp,
            remoteIpEndpoint.Port);

        context.Connected(context.RemotePeer);

        _ = Task.Run(async () =>
        {
            foreach (IChannelRequest request in context.SubDialRequests.GetConsumingEnumerable())
            {
                QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                IPeerContext dialContext = context.Fork();
                dialContext.SpecificProtocolRequest = request;
                IChannel upChannel = channelFactory.SubDial(dialContext);
                ExchangeData(stream, upChannel, request.CompletionSource);
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            QuicStream inboundStream = await connection.AcceptInboundStreamAsync(token);
            IChannel upChannel = channelFactory.SubListen(context);
            ExchangeData(inboundStream, upChannel, null);
        }
    }

    private void ExchangeData(QuicStream stream, IChannel upChannel, TaskCompletionSource? tcs)
    {
        upChannel.OnClose(async () =>
        {
            tcs?.SetResult();
            stream.Close();
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    await stream.WriteAsync(data.ToArray(), upChannel.Token);
                }
            }
            catch (SocketException)
            {
                _logger?.LogInformation("Disconnected due to a socket exception");
                await upChannel.CloseAsync(false);
            }
        }, upChannel.Token);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!upChannel.IsClosed)
                {
                    byte[] buf = new byte[1024];
                    int len = await stream.ReadAtLeastAsync(buf, 1, false, upChannel.Token);
                    if (len != 0)
                    {
                        await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
                    }
                }
            }
            catch (SocketException)
            {
                _logger?.LogInformation("Disconnected due to a socket exception");
                await upChannel.CloseAsync(false);
            }
        });
    }
}
