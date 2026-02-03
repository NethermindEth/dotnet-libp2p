// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht PutValue protocol implementation using request-response pattern.
/// </summary>
public class KadDhtPutValueProtocol : RequestResponseProtocol<PutValueRequest, PutValueResponse>
{
    public KadDhtPutValueProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/put_value", HandlePutValueRequest, loggerFactory)
    {
    }

    private static async Task<PutValueResponse> HandlePutValueRequest(PutValueRequest request, ISessionContext context)
    {
        // Note: The actual storage logic is handled by the registered handler in ServiceCollectionExtensions
        // This is just the protocol wrapper
        return new PutValueResponse
        {
            Success = true,
            Error = ""
        };
    }
}
