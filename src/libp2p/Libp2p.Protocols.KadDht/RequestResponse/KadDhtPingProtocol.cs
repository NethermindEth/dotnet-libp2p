// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht Ping protocol implementation using request-response pattern.
/// Handles DHT ping requests for connectivity testing and node discovery.
/// </summary>
public class KadDhtPingProtocol : RequestResponseProtocol<PingRequest, PingResponse>
{
    private static ILogger<KadDhtPingProtocol>? _logger;

    public KadDhtPingProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/ping", HandlePingRequest, loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger<KadDhtPingProtocol>();
    }

    private static Task<PingResponse> HandlePingRequest(PingRequest request, ISessionContext context)
    {
        try
        {
            var remotePeer = context.State.RemoteAddress?.ToString() ?? "unknown";
            _logger?.LogDebug("Processing DHT ping request from {RemotePeer}", remotePeer);

            // TODO: Add requesting peer to routing table when routing table service is available

            _logger?.LogDebug("Responding to DHT PING request with PingResponse");
            return Task.FromResult(new PingResponse());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing DHT PING request");
            return Task.FromResult(new PingResponse());
        }
    }
}
