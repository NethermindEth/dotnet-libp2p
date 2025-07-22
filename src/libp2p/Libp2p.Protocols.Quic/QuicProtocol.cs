// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Utils;
using Nethermind.Libp2p.Protocols.Quic;
using System.Buffers;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nethermind.Libp2p.Protocols;

#pragma warning disable CA1416 // Do not inform about platform compatibility
#pragma warning disable CA2252 // Do not inform about platform compatibility

/// <summary>
/// https://github.com/libp2p/specs/blob/master/quic/README.md
/// </summary>
public class QuicProtocol(ILoggerFactory? loggerFactory = null) : ITransportProtocol
{
    private readonly ILogger<QuicProtocol>? _logger = loggerFactory?.CreateLogger<QuicProtocol>();
    private readonly ECDsa _sessionKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static Multiaddress ToQuicv1MultiAddress(IPAddress a, PeerId peerId) => Multiaddress.Decode($"/{(a.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6")}/{a}/udp/0/quic-v1/p2p/{peerId}");

    private static readonly List<SslApplicationProtocol> protocols =
    [
        new SslApplicationProtocol("libp2p"),
        // SslApplicationProtocol.Http3, // webtransport
    ];

    public string Id => "quic-v1";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [.. IpHelper.GetListenerAddresses().Select(a => ToQuicv1MultiAddress(a, peerId))];

    public static bool IsAddressMatch(Multiaddress addr) => addr.Has<QUICv1>();

    public async Task ListenAsync(ITransportContext context, Multiaddress localAddr, CancellationToken token)
    {
        CheckProtocol();

        MultiaddressProtocol ipProtocol = localAddr.Has<IP4>() ? localAddr.Get<IP4>() : localAddr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int udpPort = int.Parse(localAddr.Get<UDP>().ToString());

        IPEndPoint localEndpoint = new(ipAddress, udpPort);

        X509Certificate2 cert = CertificateHelper.CertificateFromIdentity(_sessionKey, context.Peer.Identity);

        QuicServerConnectionOptions serverConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.
            MaxInboundBidirectionalStreams = 1000,
            MaxInboundUnidirectionalStreams = 1000,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ClientCertificateRequired = true,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, c, _, _) => true,
                ServerCertificate = cert,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                AllowRenegotiation = true,
                AllowTlsResume = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ServerCertificateSelectionCallback = (s, h) => cert
            },
        };

        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = localEndpoint,
            ApplicationProtocols = protocols,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverConnectionOptions)
        }, token);

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
                QuicConnection connection = await listener.AcceptConnectionAsync(token);
                INewConnectionContext clientContext = context.CreateConnection();

                _ = ProcessStreams(clientContext, connection, token).ContinueWith(t =>
                clientContext.Dispose());
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Closed with exception {exception}", ex.Message);
                _logger?.LogTrace("{stackTrace}", ex.StackTrace);
            }
        }
    }

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        CheckProtocol();

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
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                LocalCertificateSelectionCallback = (a, b, c, d, e) => c[0],
                AllowTlsResume = true,
                AllowRenegotiation = true,
                TargetHost = null,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, cert, _, _) => VerifyRemoteCertificate(remoteAddr, cert ?? throw new Libp2pException("Remote public key not found")),
                ClientCertificates = [CertificateHelper.CertificateFromIdentity(_sessionKey, context.Peer.Identity)],
            },
            RemoteEndPoint = remoteEndpoint,
        };

        QuicConnection connection = await QuicConnection.ConnectAsync(clientConnectionOptions, token);

        _logger?.Connected(connection.LocalEndPoint, connection.RemoteEndPoint);
        INewConnectionContext connectionContext = context.CreateConnection();

        token.Register(() => _ = connection.CloseAsync(0));
        await ProcessStreams(connectionContext, connection, token);
    }

    private static void CheckProtocol()
    {
        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }

        if (IsSchannel())
        {
            throw new NotSupportedException($"QUIC uses the Schannel backend, which is not supported. Check {typeof(QuicConnection).Assembly.Location}");
        }

        static bool IsSchannel()
        {
            Type quicApiType = typeof(QuicConnection).Assembly.GetType("System.Net.Quic.MsQuicApi")!;
            PropertyInfo usesSChannelBackendProperty = quicApiType.GetProperty("UsesSChannelBackend", BindingFlags.Static | BindingFlags.NonPublic)!;
            return (bool)usesSChannelBackendProperty.GetValue(null)!;
        }
    }

    private static bool VerifyRemoteCertificate(Multiaddress remoteAddr, X509Certificate certificate) =>
         CertificateHelper.ValidateCertificate(certificate as X509Certificate2 ?? throw new Libp2pException("Remote public key not found"), remoteAddr.Get<P2P>().ToString());

    private async Task ProcessStreams(INewConnectionContext context, QuicConnection connection, CancellationToken token = default)
    {
        _logger?.LogDebug("New connection to {remote}", connection.RemoteEndPoint);

        context.State.RemotePublicKey = CertificateHelper.ExtractPublicKey(connection.RemoteCertificate as X509Certificate2, out _) ?? throw new Libp2pException("Remote public key not found");
        context.State.RemoteAddress = $"/{(connection.RemoteEndPoint.AddressFamily == AddressFamily.InterNetwork ? "ip4" : "ip6")}/{connection.RemoteEndPoint.Address}/udp/{connection.RemoteEndPoint.Port}/quic-v1/p2p/{new Identity(context.State.RemotePublicKey).PeerId}";

        using INewSessionContext session = context.UpgradeToSession();

        _ = Task.Run(async () =>
        {
            foreach (UpgradeOptions upgradeOptions in session.DialRequests)
            {
                QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
                IChannel upChannel = context.Upgrade(upgradeOptions with { ModeOverride = UpgradeModeOverride.Dial });
                ExchangeData(stream, upChannel);
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            QuicStream inboundStream = await connection.AcceptInboundStreamAsync(token);
            IChannel upChannel = context.Upgrade(new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
            ExchangeData(inboundStream, upChannel);
        }
    }

    private void ExchangeData(QuicStream stream, IChannel upChannel)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            stream.Close();
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
