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
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
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
            QuicConnection connection;
            try
            {
                connection = await listener.AcceptConnectionAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not ArgumentException and not ObjectDisposedException
                and not QuicException { QuicError: QuicError.CallbackError or QuicError.InternalError or QuicError.OperationAborted })
            {
                _logger?.LogDebug(ex, "QUIC client handshake failed");
                continue;
            }

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

        INewSessionContext session = context.UpgradeToSession();
        using CancellationTokenSource acceptCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        ConcurrentDictionary<Task, byte> exchanges = new();
        void TrackExchange(QuicStream stream, IChannel channel)
        {
            Task exchange = ExchangeData(stream, channel, session.Token);
            exchanges.TryAdd(exchange, 0);
            _ = exchange.ContinueWith(task =>
            {
                exchanges.TryRemove(task, out _);
                _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        Task outgoing = Task.Run(async () =>
        {
            foreach (UpgradeOptions upgradeOptions in session.DialRequests)
            {
                try
                {
                    QuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, session.Token);
                    try
                    {
                        IChannel upChannel = context.Upgrade(upgradeOptions with { ModeOverride = UpgradeModeOverride.Dial });
                        TrackExchange(stream, upChannel);
                    }
                    catch
                    {
                        await stream.DisposeAsync();
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    upgradeOptions.CompletionSource?.TrySetException(ex);
                    throw;
                }
            }
        }, session.Token);

        Task<QuicStream>? pendingAccept = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                pendingAccept = connection.AcceptInboundStreamAsync(acceptCancellation.Token).AsTask();
                if (await Task.WhenAny(pendingAccept, outgoing) == outgoing)
                {
                    break;
                }

                Task<QuicStream> completedAccept = pendingAccept;
                pendingAccept = null;
                QuicStream inboundStream = await completedAccept;
                try
                {
                    IChannel upChannel = context.Upgrade(new UpgradeOptions { ModeOverride = UpgradeModeOverride.Listen });
                    TrackExchange(inboundStream, upChannel);
                }
                catch
                {
                    await inboundStream.DisposeAsync();
                    throw;
                }
            }
        }
        finally
        {
            acceptCancellation.Cancel();
            session.Dispose();
            if (pendingAccept is not null)
            {
                try
                {
                    await using QuicStream unusedStream = await pendingAccept;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "QUIC pending inbound stream closed");
                }
            }

            try
            {
                await outgoing;
            }
            catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
            {
            }
            finally
            {
                try
                {
                    await Task.WhenAll(exchanges.Keys);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "QUIC stream cleanup failed");
                }
            }
        }
    }

    private async Task ExchangeData(QuicStream stream, IChannel upChannel, CancellationToken token)
    {
        using CancellationTokenSource pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task outgoing = Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync(pumpCancellation.Token))
                {
                    await stream.WriteAsync(data.ToArray(), pumpCancellation.Token);
                }
                stream.CompleteWrites();
            }
            catch (Exception) when (pumpCancellation.IsCancellationRequested)
            {
                await upChannel.CloseAsync();
            }
            catch (ChannelAbortedException)
            {
                pumpCancellation.Cancel();
                await upChannel.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} outgoing data failed", stream.Id);
                pumpCancellation.Cancel();
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
                    int len = await stream.ReadAtLeastAsync(buf, 1, false, pumpCancellation.Token);
                    if (len == 0)
                    {
                        break;
                    }
                    if (await upChannel.WriteAsync(new ReadOnlySequence<byte>(buf.AsMemory()[..len]), pumpCancellation.Token) != IOResult.Ok)
                    {
                        break;
                    }
                }
                await upChannel.WriteEofAsync();
            }
            catch (Exception) when (pumpCancellation.IsCancellationRequested)
            {
                await upChannel.CloseAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} incoming data failed", stream.Id);
                pumpCancellation.Cancel();
                await upChannel.CloseAsync();
            }
        });

        try
        {
            await upChannel;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "QUIC stream {streamId} data exchange failed", stream.Id);
            pumpCancellation.Cancel();
        }
        finally
        {
            try
            {
                await outgoing;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} outgoing pump failed", stream.Id);
            }

            if (!incoming.IsCompleted)
            {
                pumpCancellation.Cancel();
            }

            try
            {
                await incoming;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} incoming pump failed", stream.Id);
            }

            try
            {
                await stream.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "QUIC stream {streamId} close failed", stream.Id);
            }
            _logger?.LogDebug("Stream {stream id}: Closed", stream.Id);
        }
    }
}
