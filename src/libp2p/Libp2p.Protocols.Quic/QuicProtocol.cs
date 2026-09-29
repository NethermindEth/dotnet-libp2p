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
using System.Security.Authentication;
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
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private static Multiaddress ToQuicV1MultiAddress(IPAddress a, PeerId peerId) => Multiaddress.Decode($"/{(a.AddressFamily is AddressFamily.InterNetwork ? "ip4" : "ip6")}/{a}/udp/0/quic-v1/p2p/{peerId}");

    private static readonly List<SslApplicationProtocol> protocols =
    [
        new SslApplicationProtocol("libp2p"),
        // SslApplicationProtocol.Http3, // webtransport
    ];

    public string Id => "quic-v1";

    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [.. IpHelper.GetListenerAddresses().Select(a => ToQuicV1MultiAddress(a, peerId))];

    public static bool IsAddressMatch(Multiaddress addr) => addr.Has<QUICv1>();

    public async Task ListenAsync(ITransportContext context, Multiaddress localAddr, CancellationToken token)
    {
        CheckProtocol();

        MultiaddressProtocol ipProtocol = localAddr.Has<IP4>() ? localAddr.Get<IP4>() : localAddr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        int udpPort = int.Parse(localAddr.Get<UDP>().ToString());

        IPEndPoint localEndpoint = new(ipAddress, udpPort);

        using CertificateHelper.CertificateLease certificateLease = CertificateHelper.CreateCertificateLease(_sessionKey, context.Peer.Identity);
        X509Certificate2 cert = certificateLease.Certificate;

        QuicServerConnectionOptions serverConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.
            MaxInboundBidirectionalStreams = 1000,
            MaxInboundUnidirectionalStreams = 1000,
            IdleTimeout = IdleTimeout,
            KeepAliveInterval = KeepAliveInterval,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ClientCertificateRequired = true,
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    ValidateClientCertificate(cert),
                ServerCertificate = cert,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                AllowRenegotiation = true,
                AllowTlsResume = true,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ServerCertificateSelectionCallback = (s, h) => cert
            },
        };

        await using QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
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

        while (!token.IsCancellationRequested)
        {
            try
            {
                QuicConnection connection = await listener.AcceptConnectionAsync(token);
                INewConnectionContext clientContext;
                try
                {
                    clientContext = context.CreateConnection();
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }

                _ = ProcessAcceptedConnection(clientContext, connection, token);
            }
            catch (Exception ex) when (token.IsCancellationRequested)
            {
                _logger?.LogDebug("Closed with exception {exception}", ex.Message);
                _logger?.LogTrace("{stackTrace}", ex.StackTrace);
            }
            catch (AuthenticationException ex)
            {
                _logger?.LogDebug(ex, "QUIC client authentication failed");
            }
            catch (QuicException ex) when (ex.QuicError is QuicError.ConnectionAborted
                or QuicError.ConnectionTimeout or QuicError.ConnectionIdle
                or QuicError.TransportError or QuicError.VersionNegotiationError)
            {
                _logger?.LogDebug(ex, "QUIC client connection failed");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "QUIC listener failed");
                throw;
            }
        }
    }

    private async Task ProcessAcceptedConnection(INewConnectionContext context, QuicConnection connection, CancellationToken token)
    {
        try
        {
            using (context)
            await using (connection)
            {
                await ProcessStreams(context, connection, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "QUIC connection from {remoteAddress} closed", connection.RemoteEndPoint);
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

        using CertificateHelper.CertificateLease certificateLease = CertificateHelper.CreateCertificateLease(_sessionKey, context.Peer.Identity);
        X509Certificate2 clientCertificate = certificateLease.Certificate;

        QuicClientConnectionOptions clientConnectionOptions = new()
        {
            DefaultStreamErrorCode = 0, // Protocol-dependent error code.
            DefaultCloseErrorCode = 1, // Protocol-dependent error code.
            MaxInboundUnidirectionalStreams = 256,
            MaxInboundBidirectionalStreams = 256,
            IdleTimeout = IdleTimeout,
            KeepAliveInterval = KeepAliveInterval,

            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    RevocationMode = X509RevocationMode.NoCheck,
                    VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority
                        | X509VerificationFlags.IgnoreInvalidName
                },
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
                AllowTlsResume = true,
                AllowRenegotiation = true,
                TargetHost = remoteAddr.Has<P2P>() ? remoteAddr.Get<P2P>().ToString() : "libp2p",
                ApplicationProtocols = protocols,
                RemoteCertificateValidationCallback = (_, cert, _, _) => VerifyRemoteCertificate(remoteAddr, cert),
                ClientCertificates = [clientCertificate],
            },
            RemoteEndPoint = remoteEndpoint,
        };

        QuicConnection connection;
        try
        {
            connection = await QuicConnection.ConnectAsync(clientConnectionOptions, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger?.LogWarning(e, "QUIC connection to {remoteAddress} failed.", remoteAddr);
            throw;
        }

        await using QuicConnection connected = connection;
        _logger?.Connected(connection.LocalEndPoint, connection.RemoteEndPoint);
        using INewConnectionContext connectionContext = context.CreateConnection();

        await ProcessStreams(connectionContext, connection, token);
    }

    private static void CheckProtocol()
    {
        if (!QuicListener.IsSupported)
        {
            throw new NotSupportedException("QUIC is not supported, check for presence of libmsquic and support of TLS 1.3.");
        }
    }

    private bool ValidateClientCertificate(X509Certificate? certificate)
    {
        string? failureReason = null;
        bool valid = certificate is X509Certificate2 x509
            && CertificateHelper.ValidateCertificate(x509, peerId: null, out failureReason);

        if (!valid)
        {
            _logger?.LogWarning("QUIC client certificate validation failed. Certificate type: {certificateType}; reason: {reason}", certificate?.GetType().FullName ?? "<null>", failureReason ?? "not an X509Certificate2");
        }

        return valid;
    }

    private bool VerifyRemoteCertificate(Multiaddress remoteAddr, X509Certificate? certificate)
    {
        string? failureReason = null;
        bool valid = certificate is X509Certificate2 x509
            && CertificateHelper.ValidateCertificate(x509, remoteAddr.Get<P2P>().ToString(), out failureReason);

        if (!valid)
        {
            _logger?.LogWarning("QUIC remote certificate validation failed for {remoteAddress}. Certificate type: {certificateType}; reason: {reason}", remoteAddr, certificate?.GetType().FullName ?? "<null>", failureReason ?? "not an X509Certificate2");
        }

        return valid;
    }

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
                _ = ExchangeData(stream, upChannel);
            }
        }, token);

        while (!token.IsCancellationRequested)
        {
            QuicStream inboundStream = await connection.AcceptInboundStreamAsync(token);
            IChannel upChannel = context.Upgrade(new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
            _ = ExchangeData(inboundStream, upChannel);
        }
    }

    private async Task ExchangeData(QuicStream stream, IChannel upChannel)
    {
        Task outgoing = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    await stream.WriteAsync(data.ToArray());
                }
                stream.CompleteWrites();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} outgoing data failed", stream.Id);
                await upChannel.CloseAsync();
            }
        });

        Task incoming = Task.Run(async () =>
        {
            try
            {
                while (stream.CanRead)
                {
                    byte[] buf = new byte[1024];
                    int len = await stream.ReadAtLeastAsync(buf, 1, false);
                    if (len == 0)
                    {
                        break;
                    }
                    await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]));
                }
                await upChannel.WriteEofAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} incoming data failed", stream.Id);
                await upChannel.CloseAsync();
            }
        });

        try
        {
            await upChannel;
            await outgoing;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "QUIC stream {streamId} data exchange failed", stream.Id);
        }
        finally
        {
            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} close failed", stream.Id);
            }
            try
            {
                await incoming;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} incoming pump failed", stream.Id);
            }
            _logger?.LogDebug("Stream {stream id}: Closed", stream.Id);
        }
    }
}
