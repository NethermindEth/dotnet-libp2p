// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Stack;

public class Libp2pPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ILoggerFactory? loggerFactory = null) : PeerFactory(protocolStackSettings, peerStore, loggerFactory)
{
    public override IPeer Create(Identity? identity = null) => new Libp2pPeer(protocolStackSettings, PeerStore, identity ?? new Identity(), LoggerFactory);
}

class Libp2pPeer(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, Identity identity, ILoggerFactory? loggerFactory = null) : LocalPeer(identity, peerStore, protocolStackSettings, loggerFactory)
{
    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }
}
