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

public class TlsProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null) : IProtocol
{
    private readonly ECDsa _sessionKey = ECDsa.Create();
    private readonly ILogger<TlsProtocol>? _logger = loggerFactory?.CreateLogger<TlsProtocol>();
    
    public SslApplicationProtocol? LastNegotiatedApplicationProtocol { get; private set; }

    private readonly List<SslApplicationProtocol> _protocols = multiplexerSettings is null ?
        new List<SslApplicationProtocol> { } :
        !multiplexerSettings.Multiplexers.Any() ?
        new List<SslApplicationProtocol> { } :
        multiplexerSettings.Multiplexers.Select(proto => new SslApplicationProtocol(proto.Id)).ToList();

    public string Id => "/tls/1.0.0";

    public async Task ListenAsync(IChannel downChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("Starting ListenAsync: PeerId {LocalPeerId}", context.LocalPeer.Address.Get<P2P>());
        if (channelFactory is null)
        {
            throw new ArgumentException("Protocol is not properly instantiated");
        }
        Stream str = new ChannelStream(downChannel);
        X509Certificate certificate = CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity);
        _logger?.LogDebug("Successfully created X509Certificate for PeerId {LocalPeerId}. Certificate Subject: {Subject}, Issuer: {Issuer}", context.LocalPeer.Address.Get<P2P>(), certificate.Subject, certificate.Issuer);
        SslServerAuthenticationOptions serverAuthenticationOptions = new()
        {
            ApplicationProtocols = _protocols,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => VerifyRemoteCertificate(context.RemotePeer.Address, certificate),
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
        };
        _logger?.LogTrace("SslServerAuthenticationOptions initialized with ApplicationProtocols: {Protocols}.", string.Join(", ", _protocols.Select(p => p.Protocol)));
        SslStream sslStream = new(str, false, serverAuthenticationOptions.RemoteCertificateValidationCallback);
        _logger?.LogTrace("SslStream initialized.");
        try
        {
            await sslStream.AuthenticateAsServerAsync(serverAuthenticationOptions);
            _logger?.LogInformation("Server TLS Authentication successful. PeerId: {RemotePeerId}, NegotiatedProtocol: {Protocol}.", context.RemotePeer.Address.Get<P2P>(), sslStream.NegotiatedApplicationProtocol.Protocol);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Error during TLS authentication for PeerId {RemotePeerId}: {ErrorMessage}.", context.RemotePeer.Address.Get<P2P>(), ex.Message);
            _logger?.LogDebug("TLS Authentication Exception Details: {StackTrace}", ex.StackTrace);
            throw;
        }
        _logger?.LogDebug($"{Encoding.UTF8.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.ToArray())} protocol negotiated");
        IChannel upChannel = channelFactory.SubListen(context);
        await ExchangeData(sslStream, upChannel, _logger);
        _ = upChannel.CloseAsync();
    }

    private static bool VerifyRemoteCertificate(Multiaddress remotePeerAddress, X509Certificate certificate) =>
        CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeerAddress.Get<P2P>().ToString());

    public async Task DialAsync(IChannel downChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("Starting DialAsync: LocalPeerId {LocalPeerId}", context.LocalPeer.Address.Get<P2P>());
        if (channelFactory is null)
        {
            throw new ArgumentException("Protocol is not properly instantiated");
        }
        Multiaddress addr = context.LocalPeer.Address;
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
            ApplicationProtocols = _protocols,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => VerifyRemoteCertificate(context.RemotePeer.Address, certificate),
            ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity) },
        };
        _logger?.LogTrace("SslClientAuthenticationOptions initialized for PeerId {RemotePeerId}.", context.RemotePeer.Address.Get<P2P>());
        Stream str = new ChannelStream(downChannel);
        SslStream sslStream = new(str, false, clientAuthenticationOptions.RemoteCertificateValidationCallback);
        _logger?.LogTrace("Sslstream initialized.");
        try
        {
            await sslStream.AuthenticateAsClientAsync(clientAuthenticationOptions);
            _logger?.LogInformation("Client TLS Authentication successful. RemotePeerId: {RemotePeerId}, NegotiatedProtocol: {Protocol}.", context.RemotePeer.Address.Get<P2P>(), sslStream.NegotiatedApplicationProtocol.Protocol);
        }
        catch (Exception ex)
        {
            _logger?.LogError("Error during TLS client authentication for RemotePeerId {RemotePeerId}: {ErrorMessage}.", context.RemotePeer.Address.Get<P2P>(), ex.Message);
            _logger?.LogDebug("TLS Authentication Exception Details: {StackTrace}", ex.StackTrace);
            return;
        }
        _logger?.LogDebug("Subdialing protocols: {Protocols}.", string.Join(", ", channelFactory.SubProtocols.Select(x => x.Id)));
        IChannel upChannel = channelFactory.SubDial(context);
        _logger?.LogDebug("SubDial completed for PeerId {RemotePeerId}.", context.RemotePeer.Address.Get<P2P>());
        await ExchangeData(sslStream, upChannel, _logger);
        _logger?.LogDebug("Connection closed for PeerId {RemotePeerId}.", context.RemotePeer.Address.Get<P2P>());
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
                    logger.LogDebug($"Got data to send to peer: {{{Encoding.UTF8.GetString(data).Replace("\n", "\\n").Replace("\r", "\\r")}}}!");
                    await sslStream.WriteAsync(data.ToArray());
                    await sslStream.FlushAsync();
                    logger.LogDebug($"Data sent to sslStream {{{Encoding.UTF8.GetString(data).Replace("\n", "\\n").Replace("\r", "\\r")}}}!!");
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
                    if (len != 0)
                    {
                        logger?.LogDebug($"Received {len} bytes from sslStream: {{{Encoding.UTF8.GetString(data, 0, len).Replace("\r", "\\r").Replace("\n", "\\n")}}}");
                        try
                        {
                            await upChannel.WriteAsync(new ReadOnlySequence<byte>(data.ToArray()[..len]));
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError(ex, "Error while reading from sslStream");
                        }
                        logger.LogDebug($"Data received from sslStream, {len}");
                    }
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

