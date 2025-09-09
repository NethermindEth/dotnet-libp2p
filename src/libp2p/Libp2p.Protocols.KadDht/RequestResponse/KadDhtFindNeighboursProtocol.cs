// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht FindNeighbours protocol implementation using request-response pattern.
/// </summary>
public class KadDhtFindNeighboursProtocol : RequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>
{
    public KadDhtFindNeighboursProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/find_node", HandleFindNeighboursRequest, loggerFactory)
    {
    }

    private static async Task<FindNeighboursResponse> HandleFindNeighboursRequest(FindNeighboursRequest request, ISessionContext context)
    {
        
        return new FindNeighboursResponse
        {
            // Neighbours = { ... }
        };
    }
}
