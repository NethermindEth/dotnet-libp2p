// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht GetProviders protocol implementation using request-response pattern.
/// </summary>
public class KadDhtGetProvidersProtocol : RequestResponseProtocol<GetProvidersRequest, GetProvidersResponse>
{
    public KadDhtGetProvidersProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/get_providers", HandleGetProvidersRequest, loggerFactory)
    {
    }

    private static async Task<GetProvidersResponse> HandleGetProvidersRequest(GetProvidersRequest request, ISessionContext context)
    {
        
        return new GetProvidersResponse
        {
            // Providers = { ... }
        };
    }
}
