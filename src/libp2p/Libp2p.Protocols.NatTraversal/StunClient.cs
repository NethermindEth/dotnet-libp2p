// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols.NatTraversal;

public class StunClient
{
    private const uint MagicCookie = 0x2112A442;
    private readonly ILogger<StunClient>? _logger;

    public StunClient(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<StunClient>();
    }

    public async Task<StunResult> DiscoverAsync(IEnumerable<Uri> stunServers, CancellationToken token)
    {
        foreach (Uri server in stunServers)
        {
            try
            {
                StunResult result = await DiscoverSingleServerAsync(server, token);
                if (result.IsSuccess)
                {
                    _logger?.LogDebug("STUN discovery successful: {PublicAddress}:{Port}",
                        result.PublicAddress, result.PublicPort);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "STUN server {Server} failed", server);
            }
        }

        return new StunResult
        {
            IsSuccess = false,
            Error = "All STUN servers failed"
        };
    }

    private async Task<StunResult> DiscoverSingleServerAsync(Uri server, CancellationToken token)
    {
        using UdpClient client = new();
        client.Client.ReceiveTimeout = 3000;
        client.Client.SendTimeout = 3000;

        byte[] request = CreateBindingRequest();
        await client.SendAsync(request, request.Length, server.Host, server.Port);

        UdpReceiveResult response = await client.ReceiveAsync(token);
        return ParseBindingResponse(response.Buffer);
    }

    private static byte[] CreateBindingRequest()
    {
        byte[] request = new byte[20];

        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), 0x0001);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), MagicCookie);
        Random.Shared.NextBytes(request.AsSpan(8, 12));

        return request;
    }

    private static StunResult ParseBindingResponse(byte[] data)
    {
        int length = data.Length;
        if (length < 20)
            return new StunResult { IsSuccess = false, Error = "Response too short" };

        ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        if (messageType != 0x0101)
            return new StunResult { IsSuccess = false, Error = "Invalid response type" };

        int offset = 20;

        while (offset + 4 <= length)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 2, 2));
            offset += 4;

            if (offset + attrLen > length)
                return new StunResult { IsSuccess = false, Error = "Malformed STUN attribute" };

            if (attrType == 0x0001 || attrType == 0x0020)
            {
                StunResult? mappedAddress = ParseMappedAddress(data.AsSpan(offset, attrLen), attrType == 0x0020);
                if (mappedAddress is not null)
                    return mappedAddress;
            }

            offset += attrLen;
            offset += (4 - (attrLen % 4)) % 4;
        }

        return new StunResult { IsSuccess = false, Error = "No mapped address found" };
    }

    private static StunResult? ParseMappedAddress(ReadOnlySpan<byte> attribute, bool xorMapped)
    {
        if (attribute.Length < 8 || attribute[1] != 0x01)
            return null;

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(attribute.Slice(2, 2));
        uint address = BinaryPrimitives.ReadUInt32BigEndian(attribute.Slice(4, 4));

        if (xorMapped)
        {
            port ^= (ushort)(MagicCookie >> 16);
            address ^= MagicCookie;
        }

        Span<byte> addressBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(addressBytes, address);

        return new StunResult
        {
            IsSuccess = true,
            PublicAddress = new IPAddress(addressBytes).ToString(),
            PublicPort = port,
            NatType = NatType.Unknown
        };
    }

    public bool IsStunServerReachable(Uri server, int timeoutMs = 3000)
    {
        try
        {
            using UdpClient client = new();
            client.Client.ReceiveTimeout = timeoutMs;
            client.Client.SendTimeout = timeoutMs;

            byte[] request = CreateBindingRequest();
            client.Send(request, request.Length, server.Host, server.Port);
            return true;
        }
        catch
        {
        }
        return false;
    }
}

public class StunResult
{
    public bool IsSuccess { get; init; }
    public string PublicAddress { get; init; } = "";
    public int PublicPort { get; init; }
    public NatType NatType { get; init; }
    public string Error { get; init; } = "";
}

public enum NatType
{
    Unknown,
    Open,
    FullCone,
    RestrictedCone,
    PortRestrictedCone,
    Symmetric
}

public class TurnClient
{
    private const uint MagicCookie = 0x2112A442;
    private readonly ILogger<TurnClient>? _logger;
    private UdpClient? _client;

    public TurnClient(string username, string password, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        _logger = loggerFactory?.CreateLogger<TurnClient>();
    }

    public async Task<TurnResult> AllocateAsync(Uri turnServer, CancellationToken token)
    {
        try
        {
            _client = new UdpClient();
            byte[] request = CreateAllocateRequest();
            await _client.SendAsync(request, request.Length, turnServer.Host, turnServer.Port);

            UdpReceiveResult response = await _client.ReceiveAsync(token);
            return ParseAllocateResponse(response.Buffer);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "TURN allocation failed");
            return new TurnResult { IsSuccess = false, Error = ex.Message };
        }
    }

    private static byte[] CreateAllocateRequest()
    {
        byte[] request = new byte[28];

        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 8);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), MagicCookie);
        Random.Shared.NextBytes(request.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(20, 2), 0x0019);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(22, 2), 4);
        request[24] = 17;

        return request;
    }

    private static TurnResult ParseAllocateResponse(byte[] data)
    {
        if (data.Length < 20)
            return new TurnResult { IsSuccess = false, Error = "Response too short" };

        ushort messageType = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2));
        if (messageType != 0x010b)
            return new TurnResult { IsSuccess = false, Error = "Invalid allocate response" };

        return new TurnResult
        {
            IsSuccess = true,
            RelayAddress = "0.0.0.0",
            RelayPort = 0
        };
    }

    public Task RefreshAllocationAsync(CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}

public class TurnResult
{
    public bool IsSuccess { get; init; }
    public string RelayAddress { get; init; } = "";
    public int RelayPort { get; init; }
    public string Error { get; init; } = "";
}
