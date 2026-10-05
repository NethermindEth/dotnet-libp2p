// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using SIPSorcery.Net;
using System.Net;
using System.Net.Sockets;
using TlsCertificate = Org.BouncyCastle.Tls.Certificate;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal sealed class WebRtcDirectIceLiteConnection : IAsyncDisposable
{
    private const ushort SctpPort = 5000;

    private readonly UdpClient _udp;
    private readonly IPEndPoint _remoteEndpoint;
    private readonly IPEndPoint _localEndpoint;
    private readonly RTCCertificate2 _certificate;
    private readonly ILogger? _logger;
    private readonly object _sendLock;
    private readonly CancellationToken _token;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _connectionCts = new();
    private DtlsSrtpTransport? _dtls;
    private RTCSctpTransport? _sctp;
    private int _disposed;

    public WebRtcDirectIceLiteConnection(
        UdpClient udp,
        IPEndPoint localEndpoint,
        IPEndPoint remoteEndpoint,
        RTCCertificate2 certificate,
        string iceCredential,
        object sendLock,
        ILogger? logger,
        CancellationToken token)
    {
        _udp = udp;
        _localEndpoint = localEndpoint;
        _remoteEndpoint = remoteEndpoint;
        _certificate = certificate;
        IceCredential = iceCredential;
        _sendLock = sendLock;
        _logger = logger;
        _token = token;
    }

    public string IceCredential { get; }

    public Task Completion => _completion.Task;

    public void Start(Func<WebRtcDirectSctpMuxer, DtlsFingerprint, CancellationToken, Task> onConnected)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(onConnected);
                _completion.TrySetResult();
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested || _connectionCts.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_token.IsCancellationRequested ? _token : _connectionCts.Token);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "WebRTC-Direct ICE-lite connection failed for {Remote}", _remoteEndpoint);
                _completion.TrySetException(ex);
            }
        }, CancellationToken.None);
    }

    public void HandleDatagram(ReadOnlySpan<byte> packet)
    {
        if (_disposed == 1)
        {
            return;
        }

        byte[] copy = packet.ToArray();
        try
        {
            _dtls?.WriteToRecvStream(copy);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to pass DTLS datagram from {Remote}", _remoteEndpoint);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _connectionCts.Cancel();

        try
        {
            _sctp?.Close();
        }
        catch
        {
        }

        try
        {
            _dtls?.Dispose();
        }
        catch
        {
        }

        _connectionCts.Dispose();
        await Task.CompletedTask;
    }

    private async Task RunAsync(Func<WebRtcDirectSctpMuxer, DtlsFingerprint, CancellationToken, Task> onConnected)
    {
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, _connectionCts.Token);

        BcTlsCrypto crypto = new();
        TlsCertificate certificateChain = new(new[] { new BcTlsCertificate(crypto, _certificate.Certificate.CertificateStructure) });
        DtlsSrtpServer server = new(crypto, certificateChain, _certificate.PrivateKey);
        _dtls = new DtlsSrtpTransport(server);
        _dtls.OnDataReady += SendDatagram;

        _logger?.LogDebug("Starting WebRTC-Direct DTLS server handshake for {Remote}.", _remoteEndpoint);
        using CancellationTokenRegistration cancellation = linkedCts.Token.Register(() => _dtls?.Dispose());
        bool handshakeResult = await Task.Run(() => _dtls.DoHandshake(out string? handshakeError), linkedCts.Token);
        if (!handshakeResult)
        {
            throw new InvalidOperationException("DTLS handshake failed for browser WebRTC-Direct listener.");
        }

        DtlsFingerprint remoteFingerprint = DtlsFingerprint.FromRtcFingerprint(
            DtlsUtils.Fingerprint("sha-256", _dtls.GetRemoteCertificate().GetCertificateAt(0)));

        _logger?.LogDebug("WebRTC-Direct DTLS handshake completed for {Remote}.", _remoteEndpoint);

        _sctp = new RTCSctpTransport(SctpPort, SctpPort, _localEndpoint.Port);
        await using WebRtcDirectSctpMuxer muxer = new(_sctp, isDtlsClient: false);
        _sctp.Start(_dtls.Transport, isDtlsClient: false);
        _sctp.Associate();

        _logger?.LogDebug("Started WebRTC-Direct SCTP transport for {Remote}.", _remoteEndpoint);
        await onConnected(muxer, remoteFingerprint, linkedCts.Token);
    }

    private void SendDatagram(byte[] packet)
    {
        if (_disposed == 1)
        {
            return;
        }

        try
        {
            lock (_sendLock)
            {
                _udp.Send(packet, packet.Length, _remoteEndpoint);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to send WebRTC-Direct packet to {Remote}", _remoteEndpoint);
        }
    }
}
