// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht Ping protocol implementation using request-response pattern.
/// </summary>
public class KadDhtPingProtocol : RequestResponseProtocol<PingRequest, PingResponse>
{
    public KadDhtPingProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/ping", HandlePingRequest, loggerFactory)
    {
    }

    private static async Task<PingResponse> HandlePingRequest(PingRequest request, ISessionContext context)
    {
        
        return new PingResponse
        {
            // To do: Indicate success or other status if needed
        };
    }
}
