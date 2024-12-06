using System.Buffers;
using System.Net;
using System.Net.Security;
using Nethermind.Libp2p.Protocols.Quic;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using System.Text;

namespace Nethermind.Libp2p.Protocols;

public class TlsProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null) : IConnectionProtocol
{
    private readonly ECDsa _sessionKey = ECDsa.Create();
    private readonly ILogger<TlsProtocol>? _logger = loggerFactory?.CreateLogger<TlsProtocol>();

    public Lazy<List<SslApplicationProtocol>> ApplicationProtocols = new(() => multiplexerSettings?.Multiplexers.Select(proto => new SslApplicationProtocol(proto.Id)).ToList() ?? []);
    public SslApplicationProtocol? LastNegotiatedApplicationProtocol { get; private set; }
    public string Id => "/tls/1.0.0";

    public async Task ListenAsync(IChannel downChannel, IConnectionContext context)
    {
        _logger?.LogInformation("Starting ListenAsync: PeerId {LocalPeerId}", context.Peer.Identity.PeerId);

        Stream str = new ChannelStream(downChannel);
        X509Certificate certificate = CertificateHelper.CertificateFromIdentity(_sessionKey, context.Peer.Identity);
        _logger?.LogDebug("Successfully created X509Certificate for PeerId {LocalPeerId}. Certificate Subject: {Subject}, Issuer: {Issuer}", context.Peer.Identity.PeerId, certificate.Subject, certificate.Issuer);


        SslServerAuthenticationOptions serverAuthenticationOptions = new()
        {
            ApplicationProtocols = ApplicationProtocols.Value,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => VerifyRemoteCertificate(context.State.RemoteAddress, certificate),
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
        };
        _logger?.LogTrace("SslServerAuthenticationOptions initialized with ApplicationProtocols: {Protocols}.", string.Join(", ", ApplicationProtocols.Value));
        SslStream sslStream = new(str, false, serverAuthenticationOptions.RemoteCertificateValidationCallback);
        _logger?.LogTrace("SslStream initialized.");
        try
        {
            await sslStream.AuthenticateAsServerAsync(serverAuthenticationOptions);
            _logger?.LogInformation("Server TLS Authentication successful. PeerId: {RemotePeerId}, NegotiatedProtocol: {Protocol}.", context.State.RemotePeerId, sslStream.NegotiatedApplicationProtocol.Protocol);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Error during TLS authentication for PeerId {RemotePeerId}: {ErrorMessage}.", context.State.RemotePeerId, ex.Message);
            _logger?.LogDebug("TLS Authentication Exception Details: {StackTrace}", ex.StackTrace);
            throw;
        }
        _logger?.LogDebug($"{Encoding.UTF8.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.ToArray())} protocol negotiated");
        IChannel upChannel = context.Upgrade();
        await ExchangeData(sslStream, upChannel, _logger);
        _ = upChannel.CloseAsync();
    }

    private static bool VerifyRemoteCertificate(Multiaddress remotePeerAddress, X509Certificate certificate) =>
        CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeerAddress.Get<P2P>().ToString());

    public async Task DialAsync(IChannel downChannel, IConnectionContext context)
    {
        _logger?.LogInformation("Starting DialAsync: LocalPeerId {LocalPeerId}", context.Peer.Identity.PeerId);

        // TODO
        Multiaddress addr = context.Peer.ListenAddresses.First();
        bool isIP4 = addr.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());

        SslClientAuthenticationOptions clientAuthenticationOptions = new()
        {
            CertificateChainPolicy = new X509ChainPolicy
            {
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority
            },
            TargetHost = ipAddress.ToString(),
            ApplicationProtocols = ApplicationProtocols.Value,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => VerifyRemoteCertificate(context.State.RemoteAddress, certificate),
            ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, context.Peer.Identity) },
        };
        _logger?.LogTrace("SslClientAuthenticationOptions initialized for PeerId {RemotePeerId}.", context.State.RemotePeerId);
        Stream str = new ChannelStream(downChannel);
        SslStream sslStream = new(str, false, clientAuthenticationOptions.RemoteCertificateValidationCallback);
        _logger?.LogTrace("Sslstream initialized.");
        try
        {
            await sslStream.AuthenticateAsClientAsync(clientAuthenticationOptions);
            _logger?.LogInformation("Client TLS Authentication successful. RemotePeerId: {RemotePeerId}, NegotiatedProtocol: {Protocol}.", context.State.RemotePeerId, sslStream.NegotiatedApplicationProtocol.Protocol);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Error during TLS client authentication for RemotePeerId {RemotePeerId}: {ErrorMessage}.", context.State.RemotePeerId, ex.Message);
            _logger?.LogDebug("TLS Authentication Exception Details: {StackTrace}", ex.StackTrace);
            return;
        }
        _logger?.LogDebug("Subdialing protocols: {Protocols}.", string.Join(", ", context.SubProtocols.Select(x => x.Id)));
        IChannel upChannel = context.Upgrade();
        _logger?.LogDebug("SubDial completed for PeerId {RemotePeerId}.", context.State.RemotePeerId);
        await ExchangeData(sslStream, upChannel, _logger);
        _logger?.LogDebug("Connection closed for PeerId {RemotePeerId}.", context.State.RemotePeerId);
        _ = upChannel.CloseAsync();
    }

    private static async Task ExchangeData(SslStream sslStream, IChannel upChannel, ILogger<TlsProtocol>? logger)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            sslStream.Close();
            logger?.LogDebug("Stream: Closed");
        });
        logger?.LogTrace("Starting data exchange between sslStream and upChannel.");
        Task writeTask = Task.Run(async () =>
        {
            try
            {
                logger?.LogDebug("Starting to write to sslStream");
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    logger?.LogDebug($"Got data to send to peer: {{{Encoding.UTF8.GetString(data).Replace("\n", "\\n").Replace("\r", "\\r")}}}");
                    await sslStream.WriteAsync(data.ToArray());
                    await sslStream.FlushAsync();
                    logger?.LogDebug($"Data sent to sslStream {{{Encoding.UTF8.GetString(data).Replace("\n", "\\n").Replace("\r", "\\r")}}}");
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error while writing to sslStream");
                await upChannel.CloseAsync();
            }
        });
        Task readTask = Task.Run(async () =>
        {
            try
            {
                logger?.LogDebug("Starting to read from sslStream");
                while (true)
                {
                    byte[] data = new byte[1024];
                    int len = await sslStream.ReadAtLeastAsync(data, 1, false);
                    if (len == 0)
                    {
                        break;
                    }

                    logger?.LogDebug($"Received {len} bytes from sslStream: {{{Encoding.UTF8.GetString(data, 0, len).Replace("\r", "\\r").Replace("\n", "\\n")}}}");
                    try
                    {
                        await upChannel.WriteAsync(new ReadOnlySequence<byte>(data.ToArray()[..len]));
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "Error while reading from sslStream");
                    }
                    logger?.LogDebug($"Data received from sslStream, {len}");
                }
                await upChannel.WriteEofAsync();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error while reading from sslStream");
            }
        });
        await Task.WhenAll(writeTask, readTask);
    }
}

