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
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols;

#pragma warning disable CA1416 // Do not inform about platform compatibility

/// <summary>
/// https://github.com/libp2p/specs/blob/master/quic/README.md
/// </summary>
[RequiresPreviewFeatures]
public class QuicProtocol : ITransportProtocol
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

    public string Id => "quic-v1";

    public async Task ListenAsync(ITransportContext context, Multiaddress localAddr, CancellationToken token)
    {
        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        MultiaddressProtocol ipProtocol = localAddr.Has<IP4>() ? localAddr.Get<IP4>() : localAddr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int udpPort = int.Parse(localAddr.Get<UDP>().ToString());

        IPEndPoint localEndpoint = new(ipAddress, udpPort);

        QuicServerConnectionOptions serverConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.

            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, c, _, _) => true,
                ServerCertificate = CertificateHelper.CertificateFromIdentity(_sessionKey, context.Peer.Identity)
            },
        };

        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = localEndpoint,
            ApplicationProtocols = protocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
        });

        if (udpPort == 0)
        {
            localAddr = localAddr.ReplaceOrAdd<UDP>(listener.LocalEndPoint.Port);
        }

        context.ListenerReady(localAddr);

        _logger?.ReadyToHandleConnections();

        token.Register(() => _ = listener.DisposeAsync());

        while (!token.IsCancellationRequested)
        {
            try
            {
                QuicConnection connection = await listener.AcceptConnectionAsync();
                INewConnectionContext clientContext = context.CreateConnection();

                _ = ProcessStreams(clientContext, connection, token).ContinueWith(t => clientContext.Dispose());
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Closed with exception {exception}", ex.Message);
                _logger?.LogTrace("{stackTrace}", ex.StackTrace);
            }
        }
    }

    public async Task DialAsync(INewConnectionContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        Multiaddress addr = remoteAddr;
        bool isIp4 = addr.Has<IP4>();
        MultiaddressProtocol protocol = isIp4 ? addr.Get<IP4>() : addr.Get<IP6>();

        IPAddress ipAddress = IPAddress.Parse(protocol.ToString());
        int udpPort = int.Parse(addr.Get<UDP>().ToString());

        IPEndPoint remoteEndpoint = new(ipAddress, udpPort);

        QuicClientConnectionOptions clientConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.
            MaxInboundUnidirectionalStreams = 256,
            MaxInboundBidirectionalStreams = 256,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = null,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, c, _, _) => VerifyRemoteCertificate(remoteAddr, c),
                ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, context.Peer.Identity) },
            },
            RemoteEndPoint = remoteEndpoint,
        };

        QuicConnection connection = await QuicConnection.ConnectAsync(clientConnectionOptions);

        _logger?.Connected(connection.LocalEndPoint, connection.RemoteEndPoint);

        token.Register(() => _ = connection.CloseAsync(0));
        await ProcessStreams(context, connection, token);
    }

    private static bool VerifyRemoteCertificate(Multiaddress remoteAddr, X509Certificate certificate) =>
         CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remoteAddr.Get<P2P>().ToString());

    private async Task ProcessStreams(INewConnectionContext context, QuicConnection connection, CancellationToken token = default)
    {
        _logger?.LogDebug("New connection to {remote}", connection.RemoteEndPoint);

        using INewSessionContext session = context.UpgradeToSession();

        _ = Task.Run(async () =>
        {
            foreach (ChannelRequest request in session.DialRequests)
            {
                QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                IChannel upChannel = context.Upgrade(new UpgradeOptions { SelectedProtocol = request.SubProtocol });
                ExchangeData(stream, upChannel, request.CompletionSource);
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            QuicStream inboundStream = await connection.AcceptInboundStreamAsync(token);
            IChannel upChannel = context.Upgrade();
            ExchangeData(inboundStream, upChannel, null);
        }
    }

    private void ExchangeData(QuicStream stream, IChannel upChannel, TaskCompletionSource? tcs)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            stream.Close();
            tcs?.SetResult();
            _logger?.LogDebug("Stream {stream id}: Closed", stream.Id);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    await stream.WriteAsync(data.ToArray());
                }
                stream.CompleteWrites();
            }
            catch (SocketException ex)
            {
                _logger?.SocketException(ex, ex.Message);
                await upChannel.CloseAsync();
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                while (stream.CanRead)
                {
                    byte[] buf = new byte[1024];
                    int len = await stream.ReadAtLeastAsync(buf, 1, false);
                    if (len != 0)
                    {
                        await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
                    }
                }
                await upChannel.WriteEofAsync();
            }
            catch (SocketException ex)
            {
                _logger?.SocketException(ex, ex.Message);
                await upChannel.CloseAsync();
            }
        });
    }
}
