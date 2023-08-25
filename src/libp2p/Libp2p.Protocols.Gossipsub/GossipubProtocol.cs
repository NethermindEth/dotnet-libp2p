// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Protocols.Floodsub;

namespace Nethermind.Libp2p.Protocols;

public class GossipsubProtocol : FloodsubProtocol
{
    public GossipsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : base(router, loggerFactory)
    {
    }

    public override string Id => "/meshsub/1.0.0";
}
