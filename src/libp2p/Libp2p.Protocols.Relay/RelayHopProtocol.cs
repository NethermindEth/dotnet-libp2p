// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Relay;
using Nethermind.Libp2p.Protocols.Relay.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// Circuit Relay v2 Hop protocol: reservation and connection initiation between clients and the relay.
/// Spec: <see href="https://github.com/libp2p/specs/blob/master/relay/circuit-v2.md">circuit-v2</see>.
/// </summary>
public class RelayHopProtocol : ISessionProtocol<HopMessage, HopMessage>
{
    private static readonly MessageParser<HopMessage> Parser = HopMessage.Parser;
    private readonly IRelayReservationStore _reservationStore;
    private readonly RelayStopProtocol _stopProtocol;
    private readonly ILogger<RelayHopProtocol>? _logger;

    public RelayHopProtocol(
        IRelayReservationStore reservationStore,
        RelayStopProtocol stopProtocol,
        ILoggerFactory? loggerFactory = null)
    {
        _reservationStore = reservationStore ?? throw new ArgumentNullException(nameof(reservationStore));
        _stopProtocol = stopProtocol ?? throw new ArgumentNullException(nameof(stopProtocol));
        _logger = loggerFactory?.CreateLogger<RelayHopProtocol>();
    }

    public string Id => "/libp2p/circuit/relay/0.2.0/hop";

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        HopMessage request = await channel.ReadPrefixedProtobufAsync(Parser).ConfigureAwait(false);
        PeerId? remotePeerId = context.State.RemotePeerId;
        if (remotePeerId is null)
        {
            _logger?.LogWarning("Hop Listen: no remote peer id");
            await SendHopStatusAsync(channel, Status.MalformedMessage).ConfigureAwait(false);
            return;
        }

        switch (request.Type)
        {
            case HopMessage.Types.Type.Reserve:
                await HandleReserveAsync(channel, context, remotePeerId).ConfigureAwait(false);
                break;
            case HopMessage.Types.Type.Connect:
                await HandleConnectAsync(channel, context, request).ConfigureAwait(false);
                break;
            default:
                _logger?.LogWarning("Hop Listen: unexpected type {Type}", request.Type);
                await SendHopStatusAsync(channel, Status.UnexpectedMessage).ConfigureAwait(false);
                break;
        }
    }

    public async Task<HopMessage> DialAsync(IChannel channel, ISessionContext context, HopMessage request)
    {
        if (request.Type != HopMessage.Types.Type.Reserve && request.Type != HopMessage.Types.Type.Connect)
        {
            throw new ArgumentException("HopMessage type must be RESERVE or CONNECT when dialing.", nameof(request));
        }

        await channel.WriteSizeAndProtobufAsync(request).ConfigureAwait(false);
        HopMessage response = await channel.ReadPrefixedProtobufAsync(Parser).ConfigureAwait(false);
        return response;
    }

    private async Task HandleReserveAsync(IChannel channel, ISessionContext context, PeerId peerId)
    {
        ulong expire = (ulong)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        // Relay addrs (without p2p-circuit) so the client can build circuit multiaddrs; spec 2.2.1.
        byte[][] addrs = context.Peer.ListenAddresses.Select(a => a.ToBytes()).ToArray();
        _reservationStore.Add(peerId, expire, addrs, null, context);
        _logger?.LogDebug("Hop: RESERVE accepted for peer {PeerId}", peerId);

        var response = new HopMessage
        {
            Type = HopMessage.Types.Type.Status,
            Status = Status.Ok,
            Reservation = new Reservation { Expire = expire }
        };
        foreach (byte[] a in addrs)
        {
            response.Reservation.Addrs.Add(ByteString.CopyFrom(a));
        }
        await channel.WriteSizeAndProtobufAsync(response).ConfigureAwait(false);
    }

    private async Task HandleConnectAsync(IChannel channel, ISessionContext context, HopMessage request)
    {
        if (request.Peer?.Id is null || request.Peer.Id.IsEmpty)
        {
            _logger?.LogWarning("Hop: CONNECT with no peer id");
            await SendHopStatusAsync(channel, Status.MalformedMessage).ConfigureAwait(false);
            return;
        }

        PeerId targetPeerId = new(request.Peer.Id.ToByteArray());
        ReservationEntry? entry = _reservationStore.TryGet(targetPeerId);
        if (entry is null)
        {
            _logger?.LogDebug("Hop: CONNECT to {Target} - NO_RESERVATION", targetPeerId);
            await SendHopStatusAsync(channel, Status.NoReservation).ConfigureAwait(false);
            return;
        }

        PeerId? initiatorPeerId = context.State.RemotePeerId;
        if (initiatorPeerId is null)
        {
            await SendHopStatusAsync(channel, Status.MalformedMessage).ConfigureAwait(false);
            return;
        }

        var connectStop = new StopMessage
        {
            Type = StopMessage.Types.Type.Connect,
            Peer = new Peer { Id = ByteString.CopyFrom(initiatorPeerId.Bytes) },
            Limit = request.Limit
        };

        try
        {
            StopMessage stopResponse = await entry.SessionContext
                .DialAsync<RelayStopProtocol, StopMessage, StopMessage>(connectStop)
                .ConfigureAwait(false);

            if (stopResponse.Status != Status.Ok)
            {
                _logger?.LogDebug("Hop: CONNECT to {Target} - stop returned {Status}", targetPeerId, stopResponse.Status);
                await SendHopStatusAsync(channel, stopResponse.Status).ConfigureAwait(false);
                return;
            }

            _logger?.LogDebug("Hop: CONNECT to {Target} - OK", targetPeerId);
            await SendHopStatusAsync(channel, Status.Ok, request.Limit).ConfigureAwait(false);
            // TODO: Bridge hop channel and stop stream for full relayed connection (requires access to stop channel from DialAsync)
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Hop: CONNECT to {Target} failed", targetPeerId);
            await SendHopStatusAsync(channel, Status.ConnectionFailed).ConfigureAwait(false);
        }
    }

    private static async Task SendHopStatusAsync(IChannel channel, Status status, Limit? limit = null)
    {
        var msg = new HopMessage
        {
            Type = HopMessage.Types.Type.Status,
            Status = status,
            Limit = limit
        };
        await channel.WriteSizeAndProtobufAsync(msg).ConfigureAwait(false);
    }
}
