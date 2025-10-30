// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht FindNeighbours protocol implementation using request-response pattern.
/// Handles DHT find_node requests and returns closest known peers from the routing table.
/// </summary>
public class KadDhtFindNeighboursProtocol : RequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>
{
    private static ILogger<KadDhtFindNeighboursProtocol>? _logger;

    public KadDhtFindNeighboursProtocol(
        ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/find_node", HandleFindNeighboursRequest, loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger<KadDhtFindNeighboursProtocol>();
    }

    private static Task<FindNeighboursResponse> HandleFindNeighboursRequest(FindNeighboursRequest request, ISessionContext context)
    {
        try
        {
            _logger?.LogDebug("Processing DHT find_node request for target {TargetKey} from {RemotePeer}",
                request.Target?.Value?.ToByteArray() != null ? Convert.ToHexString(request.Target.Value.ToByteArray()[..Math.Min(8, request.Target.Value.Length)]) : "null",
                context.State.RemoteAddress?.ToString() ?? "unknown");

            var response = new FindNeighboursResponse();

            // TODO: Add requesting peer to routing table when available
            // TODO: Query routing table for K nearest neighbors and populate response

            _logger?.LogDebug("Returning {Count} neighbours in find_node response", response.Neighbours.Count);

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error processing DHT find_node request");

            return Task.FromResult(new FindNeighboursResponse());
        }
    }
}
