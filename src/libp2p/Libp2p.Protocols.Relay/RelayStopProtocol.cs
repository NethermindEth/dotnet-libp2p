// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Relay.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
/// Circuit Relay v2 Stop protocol: governs connection termination between the relay and the target peer.
/// Spec: <see href="https://github.com/libp2p/specs/blob/master/relay/circuit-v2.md">circuit-v2</see>.
/// </summary>
public class RelayStopProtocol : ISessionProtocol<StopMessage, StopMessage>
{
    private static readonly MessageParser<StopMessage> Parser = StopMessage.Parser;
    private readonly ILogger<RelayStopProtocol>? _logger;

    public RelayStopProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<RelayStopProtocol>();
    }

    public string Id => "/libp2p/circuit/relay/0.2.0/stop";

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        StopMessage request = await channel.ReadPrefixedProtobufAsync(Parser).ConfigureAwait(false);
        if (request.Type != StopMessage.Types.Type.Connect)
        {
            _logger?.LogWarning("Stop Listen: unexpected type {Type}", request.Type);
            await SendStatusAsync(channel, Status.UnexpectedMessage).ConfigureAwait(false);
            return;
        }

        _logger?.LogDebug("Stop Listen: CONNECT from initiator {PeerId}", request.Peer?.Id != null ? Convert.ToHexString(request.Peer.Id.ToByteArray()) : "?");
        await SendStatusAsync(channel, Status.Ok).ConfigureAwait(false);
        _logger?.LogDebug("Stop Listen: STATUS OK sent, stream is now relayed connection");
    }

    public async Task<StopMessage> DialAsync(IChannel channel, ISessionContext context, StopMessage request)
    {
        if (request.Type != StopMessage.Types.Type.Connect)
        {
            throw new ArgumentException("StopMessage must be CONNECT when dialing.", nameof(request));
        }

        _logger?.LogDebug("Stop Dial: sending CONNECT");
        await channel.WriteSizeAndProtobufAsync(request).ConfigureAwait(false);
        StopMessage response = await channel.ReadPrefixedProtobufAsync(Parser).ConfigureAwait(false);
        _logger?.LogDebug("Stop Dial: received STATUS {Status}", response.Status);
        return response;
    }

    private static async Task SendStatusAsync(IChannel channel, Status status)
    {
        var msg = new StopMessage
        {
            Type = StopMessage.Types.Type.Status,
            Status = status
        };
        await channel.WriteSizeAndProtobufAsync(msg).ConfigureAwait(false);
    }
}
