// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier, ILoggerFactory? loggerFactory = null) : PeerFactory(protocolStackSettings, peerStore, loggerFactory)
{
    public override ILocalPeer Create(Identity? identity = null) => new Libp2pPeer(protocolStackSettings, PeerStore, identity ?? new Identity(), identifyNotifier, LoggerFactory);
}

class Libp2pPeer : LocalPeer
{
    public Libp2pPeer(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, Identity identity, IdentifyNotifier identifyNotifier, ILoggerFactory? loggerFactory = null) : base(identity, peerStore, protocolStackSettings, loggerFactory)
    {
        identifyNotifier.TrackChanges(this);
    }

    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }
}
