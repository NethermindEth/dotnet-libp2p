// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.RequestResponse;

/// <summary>
/// KadDht GetValue protocol implementation using request-response pattern.
/// </summary>
public class KadDhtGetValueProtocol : RequestResponseProtocol<GetValueRequest, GetValueResponse>
{
    public KadDhtGetValueProtocol(ILoggerFactory? loggerFactory = null)
        : base("/ipfs/kad/1.0.0/get_value", HandleGetValueRequest, loggerFactory)
    {
    }

    private static async Task<GetValueResponse> HandleGetValueRequest(GetValueRequest request, ISessionContext context)
    {

        return new GetValueResponse
        {
            Found = false,
            Value = Google.Protobuf.ByteString.Empty
        };
    }
}
