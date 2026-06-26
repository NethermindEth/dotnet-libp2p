// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nethermind.Libp2p.Protocols.I2p;

public sealed class I2pDatagramSession : IAsyncDisposable
{
    private static readonly Encoding SamEncoding = Encoding.ASCII;
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _samUdpEndpoint;
    private readonly int _maxPayloadSize;
    private readonly Func<ValueTask> _releaseAsync;
    private int _disposed;

    internal I2pDatagramSession(
        string sessionId,
        string publicDestination,
        UdpClient udpClient,
        IPEndPoint samUdpEndpoint,
        int maxPayloadSize,
        Func<ValueTask>? releaseAsync = null)
    {
        SessionId = sessionId;
        PublicDestination = publicDestination;
        LocalEndpoint = (IPEndPoint)udpClient.Client.LocalEndPoint!;
        _udpClient = udpClient;
        _samUdpEndpoint = samUdpEndpoint;
        _maxPayloadSize = maxPayloadSize;
        _releaseAsync = releaseAsync ?? (() => ValueTask.CompletedTask);
    }

    public string SessionId { get; }
    public string PublicDestination { get; }
    public IPEndPoint LocalEndpoint { get; }

    public async Task SendAsync(string destination, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateDatagramDestination(destination, nameof(destination));
        if (payload.Length == 0)
        {
            throw new ArgumentException("I2P datagram payload cannot be empty.", nameof(payload));
        }
        if (payload.Length > _maxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length, $"I2P datagram payload cannot exceed {_maxPayloadSize} bytes.");
        }

        byte[] packet = BuildOutboundPacket(SessionId, destination, payload.Span);
        await _udpClient.Client.SendToAsync(packet, SocketFlags.None, _samUdpEndpoint, token).ConfigureAwait(false);
    }

    public async Task<I2pDatagram> ReceiveAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        UdpReceiveResult received = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
        if (!Equals(received.RemoteEndPoint, _samUdpEndpoint))
        {
            throw new I2pException($"Rejected SAM datagram from unexpected endpoint {received.RemoteEndPoint}.");
        }

        return I2pDatagram.ParseForwarded(received.Buffer);
    }

    internal static byte[] BuildOutboundPacket(string sessionId, string destination, ReadOnlySpan<byte> payload)
    {
        ValidateHeaderToken(sessionId, nameof(sessionId));
        ValidateDatagramDestination(destination, nameof(destination));
        byte[] header = SamEncoding.GetBytes($"3.0 {sessionId} {destination}\n");
        byte[] packet = new byte[header.Length + payload.Length];
        header.CopyTo(packet, 0);
        payload.CopyTo(packet.AsSpan(header.Length));
        return packet;
    }

    private static void ValidateHeaderToken(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("SAM UDP header token cannot contain whitespace or control characters.", paramName);
        }
    }

    private static void ValidateDatagramDestination(string value, string paramName)
    {
        ValidateHeaderToken(value, paramName);
        if (value.Any(static c => c is '+' or '/' or '='))
        {
            throw new ArgumentException("SAM DATAGRAM destination must use the I2P base64 alphabet.", paramName);
        }

        string base64 = value.Replace('-', '+').Replace('~', '/');
        int remainder = base64.Length % 4;
        if (remainder == 1)
        {
            throw new ArgumentException("SAM DATAGRAM destination must be a full base64 I2P destination.", paramName);
        }
        if (remainder != 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - remainder, '=');
        }

        try
        {
            if (Convert.FromBase64String(base64).Length < 387)
            {
                throw new ArgumentException("SAM DATAGRAM destination must be a full base64 I2P destination.", paramName);
            }
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("SAM DATAGRAM destination must be a full base64 I2P destination.", paramName, ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _udpClient.Dispose();
        await _releaseAsync().ConfigureAwait(false);
    }
}
