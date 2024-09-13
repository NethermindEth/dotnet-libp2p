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

public class TlsProtocol : IProtocol
{
    public readonly ILogger<TlsProtocol>? _logger;
    private readonly ECDsa _sessionKey;
    public SslApplicationProtocol? LastNegotiatedApplicationProtocol { get; private set; }
    private readonly List<SslApplicationProtocol> _protocols;

    public TlsProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<TlsProtocol>();
        _logger?.LogDebug("TlsProtocol instantiated");
        _sessionKey = ECDsa.Create();
    }

    public string Id => "/tls/1.0.0";



    public async Task ListenAsync(IChannel downChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogDebug("Handling connection");
        _logger?.LogTrace("Successfully created client authentication options.");
        if (channelFactory is null)
        {
            throw new ArgumentException("Protocol is not properly instantiated");
        }

        _logger?.LogDebug("Successfully received data from signaling channel.");
        _logger?.LogDebug($"Successfully received data from signaling channel. Remote peer address: {context.RemotePeer.Address}");

        Stream str = new ChannelStream(downChannel, _logger);

        string remotePeerId = await downChannel.ReadLineAsync();
        string remoteAddressString = context.RemotePeer.Address.ToString();
        string fullAddressString = $"{remoteAddressString}/p2p/{remotePeerId}";


        Multiaddress newAddressWithPeerId = Multiaddress.Decode(fullAddressString);


        _logger?.LogDebug($"New address with PeerID: {newAddressWithPeerId}");
        context.RemotePeer.Address = newAddressWithPeerId;


        _logger?.LogDebug($"Successfully received data from signaling channel. Remote peer address: {remotePeerId}");

        X509Certificate certificate = CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity);

        _logger?.LogDebug($"certificate: {certificate}");

        SslServerAuthenticationOptions serverAuthenticationOptions = new()
        {
            ApplicationProtocols = [ new SslApplicationProtocol("/yamux/1.0.0")],
            RemoteCertificateValidationCallback = (_, certificate, _, _) => VerifyRemoteCertificate(context.RemotePeer.Address, certificate),
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
        };
        _logger?.LogTrace("Successfully created client authentication options.");
        // _logger?.LogDebug("Accepted new TCP client connection from {RemoteEndPoint}", tcpClient.Client.RemoteEndPoint);
        SslStream sslStream = new(str, false, serverAuthenticationOptions.RemoteCertificateValidationCallback);
        _logger?.LogTrace("Sslstream initialized.");
        try
        {
            _logger?.LogTrace("Sslstream initialized.1");
            await sslStream.AuthenticateAsServerAsync(serverAuthenticationOptions);
            _logger?.LogTrace("Sslstream initialized.2");
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
            _logger?.LogDebug($"Protocol negotiated!");
        }

        IChannel upChannel = channelFactory.SubListen(context);

        LastNegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;


        await ExchangeData(sslStream, upChannel, _logger);
      
    }


    private static bool VerifyRemoteCertificate(Multiaddress remotePeerAddress, X509Certificate certificate) =>
             CertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeerAddress.Get<P2P>().ToString());

    public class ChannelStream(IChannel chan, ILogger<TlsProtocol> logger) : Stream
    {
        private bool _disposed = false;
        private bool _canRead = true;
        private bool _canWrite = true;

        public override bool CanRead => _canRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _canWrite;
        public override long Length => throw new Exception();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }
        public override int Read(Span<byte> buffer)
        {
            logger.LogInformation("READ 1");
            if (buffer is { Length: 0 } && _canRead)
            {
                return 0;
            }
            logger.LogInformation("READ 2");

            var result = chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny).Result;

            logger.LogInformation($"READ 3 {result.Result}");

            if (result.Result != IOResult.Ok)
            {
                _canRead = false;
                return 0;
            }
            result.Data.CopyTo(buffer);
            logger.LogInformation("READ 4");

            return (int)result.Data.Length;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            logger.LogInformation("WRITE 1");
            if (chan.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count))).Result != IOResult.Ok)
            {
                logger.LogInformation("WRITE 1.1");
                _canWrite = false;
            }
            logger.LogInformation("WRITE 2");

        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            logger.LogInformation("AWRITE 1");
            if ((await chan.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count)))) != IOResult.Ok)
            {
                logger.LogInformation("AWRITE 1.1");
                _canWrite = false;
            }
            logger.LogInformation("AWRITE 2");
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Write async 2");
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            logger.LogInformation("AREAD 1");
            if (buffer is { Length: 0 } && _canRead)
            {
                return 0;
            }
            logger.LogInformation("AREAD 2");

            var result = await chan.ReadAsync(buffer.Length, ReadBlockingMode.WaitAny);

            logger.LogInformation($"AREAD 3 {result.Result}");

            if (result.Result != IOResult.Ok)
            {
                _canRead = false;
                return 0;
            }
            result.Data.CopyTo(buffer);
            logger.LogInformation("READ 4");

            return (int)result.Data.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            logger.LogInformation("Read async 2");
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _ = chan.CloseAsync();
                }
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }

    public async Task DialAsync(IChannel downChannel, IChannelFactory? channelFactory, IPeerContext context)
    {
        _logger?.LogInformation("Handling connection");
        if (channelFactory is null)
        {
            throw new ArgumentException("Protocol is not properly instantiated");
        }

        var localPeeraddress = context.LocalPeer.Address.Get<P2P>();
        string localPeerId = localPeeraddress.ToString();
        // await downChannel.WriteLineAsync(localPeerId);
        var peerIdComponent = context.RemotePeer.Address.Get<P2P>();
        string peerId = peerIdComponent.ToString();
        _logger?.LogTrace("peerId: {peerId}", peerId);
        Multiaddress addr = context.LocalPeer.Address;
        bool isIP4 = addr.Has<IP4>();
        MultiaddressProtocol ipProtocol = isIP4 ? addr.Get<IP4>() : addr.Get<IP6>();
        IPAddress ipAddress = IPAddress.Parse(ipProtocol.ToString());
        SslClientAuthenticationOptions clientAuthenticationOptions = new()
        {
            CertificateChainPolicy = new X509ChainPolicy
            {
                RevocationMode = X509RevocationMode.NoCheck,   // Disable certificate revocation check
                VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority // Allow self-signed certificates
               
            },
            TargetHost = ipAddress.ToString(),
            ApplicationProtocols = _protocols,
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, certificate, _, _) => VerifyRemoteCertificate(context.RemotePeer.Address, certificate),
            ClientCertificates = new X509CertificateCollection { CertificateHelper.CertificateFromIdentity(_sessionKey, context.LocalPeer.Identity) },
        };
        _logger?.LogTrace("Successfully created client authentication options.");


        Stream str = new ChannelStream(downChannel, _logger);
        SslStream sslStream = new(str, false, clientAuthenticationOptions.RemoteCertificateValidationCallback);
        _logger?.LogTrace("Sslstream initialized.");
        try
        {
            
            await sslStream.AuthenticateAsClientAsync(clientAuthenticationOptions);
            _logger?.LogTrace("Successfully authenticated as client.");
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
                _logger?.LogDebug($"{Encoding.UTF8.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.ToArray())} protocol negotiated");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError("An error occurred while authenticating the server: {Message}", ex.Message);
            return;
        }

        LastNegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;


        _logger?.LogDebug($"Subdialing with {string.Join(", ", channelFactory.SubProtocols.Select(x=>x.Id))}");


        IChannel upChannel = channelFactory.SubDial(context);
       
        await ExchangeData(sslStream, upChannel, _logger);
        _logger?.LogDebug($"Close");

        _ = upChannel.CloseAsync();

    }



    private static Task ExchangeData(SslStream sslStream, IChannel upChannel, ILogger<TlsProtocol>? logger)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            sslStream.Close();

            logger?.LogDebug("Stream: Closed");
        });
        Task t = Task.Run(async () =>
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

        Task t2 = Task.Run(async () =>
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
                            await upChannel.WriteAsync(new ReadOnlySequence<byte>(data.ToArray()[..len])); // Handle the case where the stream ends
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
        return Task.WhenAll(t, t2).ContinueWith((t) =>
        {
            // Close the SSL stream when both tasks are done
        });


    }

}
