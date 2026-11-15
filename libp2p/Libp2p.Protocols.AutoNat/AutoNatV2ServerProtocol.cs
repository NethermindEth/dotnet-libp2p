// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.AutoNat;
using Nethermind.Libp2p.Protocols.AutoNat.Dto;
using Nethermind.Libp2p.Protocols.Identify;
using ResponseStatus = Nethermind.Libp2p.Protocols.AutoNat.Dto.DialResponse.Types.ResponseStatus;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// AutoNAT v2 server: dials back one of the addresses a peer asks about and reports the result.
/// Spec: <see href="https://github.com/libp2p/specs/blob/master/autonat/autonat-v2.md">autonat-v2</see>.
/// </summary>
public class AutoNatV2ServerProtocol : ISessionListenerProtocol
{
    public const string ProtocolId = "/libp2p/autonat/2/dial-request";

    // Limits follow the go-libp2p AutoNAT v2 server.
    private const int MaxMessageSize = 8192;
    private const int MaxAddresses = 50;
    private const int MinDialDataBytes = 30_000;
    private const int MaxDialDataBytes = 100_000;
    private const int MinDialDataChunk = 100;
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(15);

    private readonly IAutoNatDialBack _dialBack;
    private readonly AutoNatV2ServerSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly AutoNatRateLimiter _limiter;
    private readonly ILogger<AutoNatV2ServerProtocol>? _logger;

    public AutoNatV2ServerProtocol(IAutoNatDialBack dialBack, AutoNatV2ServerSettings? settings = null, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        _dialBack = dialBack ?? throw new ArgumentNullException(nameof(dialBack));
        _settings = settings ?? new AutoNatV2ServerSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _limiter = new AutoNatRateLimiter(_settings, _timeProvider);
        _logger = loggerFactory?.CreateLogger<AutoNatV2ServerProtocol>();
    }

    public string Id => ProtocolId;

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        PeerId peer = context.State.RemotePeerId ?? throw new Libp2pException("AutoNAT v2 request without a remote peer id");

        if (!_limiter.TryAccept(peer))
        {
            _logger?.LogDebug("AutoNAT v2: rate limited request from {PeerId}", peer);
            await WriteResponseAsync(channel, new DialResponse { Status = ResponseStatus.ERequestRejected }).ConfigureAwait(false);
            return;
        }

        try
        {
            using CancellationTokenSource timeout = new(StreamTimeout, _timeProvider);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, context.UpgradeOptions?.CancellationToken ?? context.Token);
            await ServeAsync(channel, context, peer, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _limiter.Complete(peer);
        }
    }

    private async Task ServeAsync(IChannel channel, ISessionContext context, PeerId peer, CancellationToken token)
    {
        Message request = await channel.ReadPrefixedProtobufAsync(Message.Parser, MaxMessageSize, token).ConfigureAwait(false);
        if (request.MsgCase != Message.MsgOneofCase.DialRequest)
        {
            throw new InvalidDataException($"Expected a dial request, got {request.MsgCase}");
        }

        (int index, Multiaddress? addr, IPAddress? ip) = SelectAddress(request.DialRequest.Addrs, peer);
        if (addr is null)
        {
            _logger?.LogDebug("AutoNAT v2: no address to dial for {PeerId}", peer);
            await WriteResponseAsync(channel, new DialResponse { Status = ResponseStatus.EDialRefused }).ConfigureAwait(false);
            return;
        }

        // Amplification attack prevention: dialing an IP other than the requester's costs them dial data.
        if (!Equals(GetIp(context.State.RemoteAddress), ip))
        {
            if (!_limiter.TryAcceptDialData())
            {
                await WriteResponseAsync(channel, new DialResponse { Status = ResponseStatus.ERequestRejected }).ConfigureAwait(false);
                return;
            }

            await ReadDialDataAsync(channel, index, token).ConfigureAwait(false);
            // Random pause so one client can't make many servers hit a victim at the same moment.
            TimeSpan delay = TimeSpan.FromTicks(Random.Shared.NextInt64(_settings.MaxDialDataDelay.Ticks + 1));
            await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
        }

        DialStatus dialStatus;
        try
        {
            dialStatus = await _dialBack.DialBackAsync(peer, addr, request.DialRequest.Nonce, token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger?.LogWarning(e, "AutoNAT v2: dial back to {Address} failed", addr);
            await WriteResponseAsync(channel, new DialResponse { Status = ResponseStatus.EInternalError }).ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(channel, new DialResponse { Status = ResponseStatus.Ok, AddrIdx = (uint)index, DialStatus = dialStatus }).ConfigureAwait(false);
    }

    private (int Index, Multiaddress? Addr, IPAddress? Ip) SelectAddress(RepeatedField<ByteString> addrs, PeerId peer)
    {
        for (int i = 0; i < Math.Min(addrs.Count, MaxAddresses); i++)
        {
            Multiaddress addr;
            try
            {
                addr = Multiaddress.Decode(addrs[i].ToByteArray());
            }
            catch (Exception)
            {
                continue;
            }

            // shortcut: DNS addresses are refused, not resolved; resolve them first if clients start sending them.
            IPAddress? ip = GetIp(addr);
            if (ip is null || (!_settings.AllowPrivateAddresses && !IsPublic(ip))) continue;
            if (addr.GetPeerId() is { } target && target != peer) continue;
            if (!_dialBack.CanDial(addr)) continue;
            return (i, addr, ip);
        }

        return (-1, null, null);
    }

    private static async Task ReadDialDataAsync(IChannel channel, int index, CancellationToken token)
    {
        ulong numBytes = (ulong)Random.Shared.NextInt64(MinDialDataBytes, MaxDialDataBytes + 1);
        await channel.WriteSizeAndProtobufAsync(new Message { DialDataRequest = new DialDataRequest { AddrIdx = (uint)index, NumBytes = numBytes } }).ConfigureAwait(false);

        for (ulong received = 0; received < numBytes;)
        {
            Message message = await channel.ReadPrefixedProtobufAsync(Message.Parser, MaxMessageSize, token).ConfigureAwait(false);
            if (message.MsgCase != Message.MsgOneofCase.DialDataResponse)
            {
                throw new InvalidDataException($"Expected dial data, got {message.MsgCase}");
            }

            int length = message.DialDataResponse.Data.Length;
            received += (ulong)length;
            // Tiny chunks would cost us a parse each while costing the client almost nothing.
            if (length < MinDialDataChunk && received < numBytes)
            {
                throw new InvalidDataException($"Dial data chunk of {length} bytes is too small");
            }
        }
    }

    private static IPAddress? GetIp(Multiaddress? addr)
    {
        MultiaddressProtocol? protocol = addr is null ? null : addr.Has<IP4>() ? addr.Get<IP4>() : addr.Has<IP6>() ? addr.Get<IP6>() : null;
        if (protocol is null) return null;
        IPAddress ip = IPAddress.Parse(protocol.ToString());
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsPrivate() || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any)) return false;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return true;
        byte[] b = ip.GetAddressBytes();
        // 0.0.0.0/8, carrier-grade NAT 100.64.0.0/10, multicast and reserved 224.0.0.0/3
        return b[0] != 0 && !(b[0] == 100 && (b[1] & 0xC0) == 64) && b[0] < 224;
    }

    private static ValueTask WriteResponseAsync(IChannel channel, DialResponse response)
        => channel.WriteSizeAndProtobufAsync(new Message { DialResponse = response });
}
