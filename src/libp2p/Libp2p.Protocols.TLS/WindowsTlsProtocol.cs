// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// Windows-compatible TLS protocol implementation
/// </summary>
public class WindowsTlsProtocol : IProtocol, IDisposable
{
    private readonly ECDsa _sessionKey;
    private readonly ILogger<WindowsTlsProtocol>? _logger;
    private readonly MultiplexerSettings? _multiplexerSettings;

    public Lazy<List<SslApplicationProtocol>> ApplicationProtocols { get; }
    public SslApplicationProtocol? LastNegotiatedApplicationProtocol { get; private set; }
    public string Id => "/tls/1.0.0";

    public WindowsTlsProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
    {
        _multiplexerSettings = multiplexerSettings;
        _logger = loggerFactory?.CreateLogger<WindowsTlsProtocol>();
        _sessionKey = WindowsCertificateHelper.CreateWindowsCompatibleECDsa();

        ApplicationProtocols = new Lazy<List<SslApplicationProtocol>>(() =>
            multiplexerSettings?.Multiplexers.Select(proto => new SslApplicationProtocol(proto.Id)).ToList() ??
            [new SslApplicationProtocol("/yamux/1.0.0")]);
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        try
        {
            _logger?.LogDebug("TLS Listen: Starting server authentication for PeerId {LocalPeerId}.", context.LocalPeer.Identity.PeerId);

            X509Certificate2 certificate = WindowsCertificateHelper.CreateCertificateWithPrivateKey(_sessionKey, context.LocalPeer.Identity);

            var serverOptions = CreateServerAuthenticationOptions(certificate, context);

            Stream stream = new WindowsChannelStream(downChannel, _logger);
            using SslStream sslStream = new(stream, false, serverOptions.RemoteCertificateValidationCallback);

            _logger?.LogTrace("TLS Listen: Starting server authentication.");
            await sslStream.AuthenticateAsServerAsync(serverOptions);
            _logger?.LogDebug("TLS Listen: Server authentication successful.");

            LastNegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
            _logger?.LogDebug("TLS Listen: Protocol negotiated: {Protocol}",
                Encoding.UTF8.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.ToArray()));

            IChannel upChannel = upChannelFactory?.SubListen(context)!;
            await ExchangeData(sslStream, upChannel, _logger);
            _ = upChannel.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TLS Listen: Error during server authentication for PeerId {LocalPeerId}.", context.LocalPeer.Identity.PeerId);
            throw;
        }
    }

    public async Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        try
        {
            _logger?.LogDebug("TLS Dial: Starting client authentication for PeerId {RemotePeerId}.", context.RemotePeer.Identity?.PeerId);

            X509Certificate2 certificate = WindowsCertificateHelper.CreateCertificateWithPrivateKey(_sessionKey, context.LocalPeer.Identity);

            var clientOptions = CreateClientAuthenticationOptions(certificate, context);

            Stream stream = new WindowsChannelStream(downChannel, _logger);
            using SslStream sslStream = new(stream, false, clientOptions.RemoteCertificateValidationCallback);

            _logger?.LogTrace("TLS Dial: Starting client authentication.");
            await sslStream.AuthenticateAsClientAsync(clientOptions);
            _logger?.LogDebug("TLS Dial: Client authentication successful.");

            LastNegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
            _logger?.LogDebug("TLS Dial: Protocol negotiated: {Protocol}",
                Encoding.UTF8.GetString(sslStream.NegotiatedApplicationProtocol.Protocol.ToArray()));

            IChannel upChannel = upChannelFactory?.SubDial(context)!;
            await ExchangeData(sslStream, upChannel, _logger);
            _ = upChannel.CloseAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TLS Dial: Error during client authentication for PeerId {RemotePeerId}.", context.RemotePeer.Identity?.PeerId);
            throw;
        }
    }

    private SslServerAuthenticationOptions CreateServerAuthenticationOptions(X509Certificate2 certificate, IPeerContext context)
    {
        return new SslServerAuthenticationOptions
        {
            ApplicationProtocols = ApplicationProtocols.Value,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                WindowsCertificateHelper.ValidateCertificate(certificate as X509Certificate2, context.RemotePeer.Identity?.PeerId?.ToString()),
            ServerCertificate = certificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = GetSupportedSslProtocols(),
            AllowRenegotiation = false, // More secure
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        };
    }

    private SslClientAuthenticationOptions CreateClientAuthenticationOptions(X509Certificate2 certificate, IPeerContext context)
    {
        // Get target host from remote address
        string targetHost = GetTargetHostFromAddress(context.RemotePeer.Address);

        return new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            ApplicationProtocols = ApplicationProtocols.Value,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                WindowsCertificateHelper.ValidateCertificate(certificate as X509Certificate2, context.RemotePeer.Identity?.PeerId?.ToString()),
            ClientCertificates = [certificate],
            EnabledSslProtocols = GetSupportedSslProtocols(),
            AllowRenegotiation = false, // More secure
            EncryptionPolicy = EncryptionPolicy.RequireEncryption
        };
    }

    private static string GetTargetHostFromAddress(Multiaddress address)
    {
        try
        {
            if (address.Has<IP4>())
            {
                return address.Get<IP4>().ToString();
            }
            if (address.Has<IP6>())
            {
                return address.Get<IP6>().ToString();
            }
            return "localhost";
        }
        catch
        {
            return "localhost";
        }
    }

    /// <summary>
    /// Gets supported SSL protocols with Windows compatibility
    /// </summary>
    private static SslProtocols GetSupportedSslProtocols()
    {
        // Start with TLS 1.2 and 1.3 for better Windows compatibility
        var protocols = SslProtocols.Tls12;

        try
        {
            // Try to add TLS 1.3 if available
            protocols |= SslProtocols.Tls13;
        }
        catch
        {
            // TLS 1.3 might not be available on older Windows versions
        }

        return protocols;
    }

    private static bool VerifyRemoteCertificate(Multiaddress remotePeerAddress, X509Certificate certificate) =>
        WindowsCertificateHelper.ValidateCertificate(certificate as X509Certificate2, remotePeerAddress.Get<P2P>()?.ToString());

    private static async Task ExchangeData(SslStream sslStream, IChannel upChannel, ILogger<WindowsTlsProtocol>? logger)
    {
        upChannel.GetAwaiter().OnCompleted(() =>
        {
            try
            {
                sslStream.Close();
                logger?.LogDebug("TLS: SslStream closed.");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "TLS: Error closing SslStream.");
            }
        });

        logger?.LogTrace("TLS: Starting data exchange between SslStream and upChannel.");

        Task writeTask = Task.Run(async () =>
        {
            try
            {
                logger?.LogDebug("TLS: Starting to write to SslStream");
                await foreach (ReadOnlySequence<byte> data in upChannel.ReadAllAsync())
                {
                    if (logger?.IsEnabled(LogLevel.Trace) == true)
                    {
                        logger.LogTrace("TLS: Sending data to peer (length: {Length})", data.Length);
                    }

                    await sslStream.WriteAsync(data.ToArray());
                    await sslStream.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "TLS: Error writing to SslStream");
            }
        });

        Task readTask = Task.Run(async () =>
        {
            try
            {
                logger?.LogDebug("TLS: Starting to read from SslStream");
                byte[] buffer = new byte[4096];

                while (true)
                {
                    int bytesRead = await sslStream.ReadAsync(buffer);
                    if (bytesRead == 0)
                    {
                        logger?.LogDebug("TLS: End of stream reached");
                        break;
                    }

                    if (logger?.IsEnabled(LogLevel.Trace) == true)
                    {
                        logger.LogTrace("TLS: Received data from peer (length: {Length})", bytesRead);
                    }

                    await upChannel.WriteAsync(new ReadOnlySequence<byte>(buffer[..bytesRead]));
                }

                await upChannel.WriteEofAsync();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "TLS: Error reading from SslStream");
            }
        });

        await Task.WhenAll(writeTask, readTask);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sessionKey?.Dispose();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Windows-compatible channel stream implementation
/// </summary>
public class WindowsChannelStream : Stream
{
    private readonly IChannel _channel;
    private readonly ILogger? _logger;
    private bool _disposed;
    private readonly SemaphoreSlim _readSemaphore = new(1, 1);
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);

    public WindowsChannelStream(IChannel channel, ILogger? logger)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _logger = logger;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        // No action needed
    }

    public override async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        // No action needed
        await Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsChannelStream));

        await _readSemaphore.WaitAsync(cancellationToken);
        try
        {
            var result = await _channel.ReadAsync(count, ReadBlockingMode.WaitAny);
            if (result.Result == IOResult.Ok && result.Data.Length > 0)
            {
                int bytesToCopy = Math.Min(count, (int)result.Data.Length);
                result.Data.Slice(0, bytesToCopy).CopyTo(buffer.AsSpan(offset, bytesToCopy));
                return bytesToCopy;
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WindowsChannelStream: Error reading from channel");
            return 0;
        }
        finally
        {
            _readSemaphore.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WindowsChannelStream));

        await _writeSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _channel.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(offset, count)));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "WindowsChannelStream: Error writing to channel");
            throw;
        }
        finally
        {
            _writeSemaphore.Release();
        }
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
        if (!_disposed && disposing)
        {
            _readSemaphore.Dispose();
            _writeSemaphore.Dispose();
            _disposed = true;
        }
        base.Dispose(disposing);
    }
}
