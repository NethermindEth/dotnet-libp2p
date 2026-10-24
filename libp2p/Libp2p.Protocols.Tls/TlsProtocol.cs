// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using System.Net.Security;
using System.Security.Authentication;
using Nethermind.Libp2p.Protocols.Quic;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Protocols.Tls;

public class TlsProtocol : IConnectionProtocol
{
    private readonly ECDsa _sessionKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ILogger<TlsProtocol>? _logger;

    // libp2p TLS spec requires "libp2p" as ALPN protocol
    private static readonly SslApplicationProtocol Libp2pProtocol = new("libp2p");
    public static List<SslApplicationProtocol> ApplicationProtocols => [Libp2pProtocol];
    public SslApplicationProtocol? LastNegotiatedApplicationProtocol { get; private set; }
    public string Id => "/tls/1.0.0";

    public TlsProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
    {
        _ = multiplexerSettings;
        _logger = loggerFactory?.CreateLogger<TlsProtocol>();
    }

    public Task ListenAsync(IChannel downChannel, IConnectionContext context) => RunAsync(downChannel, context, server: true);

    public Task DialAsync(IChannel downChannel, IConnectionContext context) => RunAsync(downChannel, context, server: false);

    private async Task RunAsync(IChannel downChannel, IConnectionContext context, bool server)
    {
        bool authenticated = false;
        CancellationToken cancellation = context.UpgradeOptions?.CancellationToken ?? default;
        try
        {
            using CertificateHelper.CertificateLease certificateLease = CertificateHelper.CreateCertificateLease(_sessionKey, context.Peer.Identity);
            using TlsHandshake handshake = new(certificateLease.Certificate, server,
                certificate => VerifyRemoteCertificate(context.State.RemoteAddress, certificate));
            await TlsChannel.RunAsync(handshake, downChannel, () =>
            {
                // The engine has verified CertificateVerify and Finished, and our
                // final handshake output has drained. No identity is published earlier.
                SetRemoteIdentity(context, handshake.RemoteCertificate!);
                LastNegotiatedApplicationProtocol = handshake.NegotiatedProtocol;
                authenticated = true;
                IChannel channel = context.Upgrade();
                _logger?.LogDebug("TLS authenticated {RemotePeerId}", context.State.RemotePeerId);
                return channel;
            }, cancellation);
        }
        catch (OperationCanceledException)
        {
            await downChannel.AbortAsync();
            throw;
        }
        catch (Exception ex) when (!authenticated && ex is not AuthenticationException)
        {
            await downChannel.AbortAsync();
            _logger?.LogDebug(ex, "TLS authentication failed");
            throw new AuthenticationException("TLS mutual authentication failed.", ex);
        }
    }

    private static bool VerifyRemoteCertificate(Multiaddress? remotePeerAddress, X509Certificate? certificate) =>
        certificate is X509Certificate2 x509 && CertificateHelper.ValidateCertificate(x509, remotePeerAddress.GetPeerId()?.ToString());

    private static void SetRemoteIdentity(IConnectionContext context, X509Certificate2 certificate)
    {
        Core.Dto.PublicKey remotePublicKey = CertificateHelper.ExtractPublicKey(certificate, out _)
            ?? throw new Libp2pException("Remote public key not found");

        if (context.State.RemotePublicKey is { } existingRemotePublicKey && existingRemotePublicKey.ToByteString() != remotePublicKey.ToByteString())
        {
            throw new Libp2pException("TLS certificate public key does not match the previously authenticated remote public key.");
        }

        context.State.RemotePublicKey = remotePublicKey;

        if (context.State.RemoteAddress is { } remoteAddress && remoteAddress.GetPeerId() is null)
        {
            remoteAddress.Add(new P2P(new Identity(remotePublicKey).PeerId.ToString()));
        }
    }

}
