// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht AddProvider protocol implementation using request-response pattern.
/// </summary>
public class KadDhtAddProviderProtocol : RequestResponseProtocol<AddProviderRequest, AddProviderResponse>
{
    public KadDhtAddProviderProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/add_provider", HandleAddProviderRequest, loggerFactory)
    {
    }

    private static async Task<AddProviderResponse> HandleAddProviderRequest(AddProviderRequest request, ISessionContext context)
    {
        
        return new AddProviderResponse
        {
            Success = true,
            Error = ""
        };
    }
}
