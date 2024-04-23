// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Quic;
using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
//using Nethermind.Libp2p.Protocols.Quic;

namespace Nethermind.Libp2p.Protocols;

#pragma warning disable CA1416 // Do not inform about platform compatibility

/// <summary>
/// https://github.com/libp2p/specs/blob/master/quic/README.md
/// </summary>
[RequiresPreviewFeatures]
public class QuicProtocol : IProtocol
{
    private readonly ILogger<QuicProtocol>? _logger;
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

        Multiaddress addr = context.LocalPeer.Address;
        MultiaddressProtocol ipProtocol = addr.Has<IP4>() ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int udpPort = int.Parse(addr.Get<UDP>().ToString());

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

        var localEndPoint = new Multiaddress();
        // IP (4 or 6 is based on source address).
        var strLocalEndpoint = listener.LocalEndPoint.Address.ToString();
        localEndPoint = addr.Has<IP4>() ? localEndPoint.Add<IP4>(strLocalEndpoint) : localEndPoint.Add<IP6>(strLocalEndpoint);

        // UDP
        localEndPoint = localEndPoint.Add<UDP>(listener.LocalEndPoint.Port);

        // Set on context
        context.LocalEndpoint = localEndPoint;

        if (udpPort == 0)
        {
            context.LocalPeer.Address = context.LocalPeer.Address
                .ReplaceOrAdd<UDP>(listener.LocalEndPoint.Port);
        }

        channel.OnClose(async () =>
        {
            await listener.DisposeAsync();
        });

        _logger?.ReadyToHandleConnections();
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

        Multiaddress addr = context.LocalPeer.Address;
        bool isIp4 = addr.Has<IP4>();
        MultiaddressProtocol protocol = isIp4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(protocol.ToString());
        int udpPort = int.Parse(addr.Get<UDP>().ToString());

        IPEndPoint localEndpoint = new(ipAddress, udpPort);

        addr = context.RemotePeer.Address;
        isIp4 = addr.Has<IP4>();
        protocol = isIp4 ? addr.Get<IP4>() : addr.Get<IP6>();
        ipAddress = IPAddress.Parse(protocol.ToString()!);
        udpPort = int.Parse(addr.Get<UDP>().ToString()!);

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

        _logger?.Connected(connection.LocalEndPoint, connection.RemoteEndPoint);

        await ProcessStreams(connection, context, channelFactory, channel.Token);
    }

    private static bool VerifyRemoteCertificate(IPeer? remotePeer, X509Certificate certificate) =>
         CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeer?.Address.Get<P2P>().ToString());

    private async Task ProcessStreams(QuicConnection connection, IPeerContext context, IChannelFactory channelFactory, CancellationToken token)
    {
        bool isIP4 = connection.LocalEndPoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress localEndPointMultiaddress = new Multiaddress();
        string strLocalEndpointAddress = connection.LocalEndPoint.Address.ToString();
        localEndPointMultiaddress = isIP4 ? localEndPointMultiaddress.Add<IP4>(strLocalEndpointAddress) : localEndPointMultiaddress.Add<IP6>(strLocalEndpointAddress);
        localEndPointMultiaddress = localEndPointMultiaddress.Add<UDP>(connection.LocalEndPoint.Port);

        context.LocalEndpoint = localEndPointMultiaddress;

        context.LocalPeer.Address = isIP4 ? context.LocalPeer.Address.ReplaceOrAdd<IP4>(strLocalEndpointAddress) : context.LocalPeer.Address.ReplaceOrAdd<IP6>(strLocalEndpointAddress);

        IPEndPoint remoteIpEndpoint = connection.RemoteEndPoint!;
        isIP4 = remoteIpEndpoint.AddressFamily == AddressFamily.InterNetwork;

        Multiaddress remoteEndPointMultiaddress = new Multiaddress();
        string strRemoteEndpointAddress = remoteIpEndpoint.Address.ToString();
        remoteEndPointMultiaddress = isIP4 ? remoteEndPointMultiaddress.Add<IP4>(strRemoteEndpointAddress) : remoteEndPointMultiaddress.Add<IP6>(strRemoteEndpointAddress);
        remoteEndPointMultiaddress = remoteEndPointMultiaddress.Add<UDP>(remoteIpEndpoint.Port);

        context.RemoteEndpoint = remoteEndPointMultiaddress;

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
            catch (SocketException ex)
            {
                _logger?.SocketException(ex, ex.Message);
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
            catch (SocketException ex)
            {
                _logger?.SocketException(ex, ex.Message);
                await upChannel.CloseAsync(false);
            }
        });
    }
}
