// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using System.Diagnostics;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier, ActivitySource? activitySource = null, Activity? rootActivity = null, ILoggerFactory? loggerFactory = null) : PeerFactory(protocolStackSettings, peerStore, activitySource, rootActivity, loggerFactory)
{
    public override ILocalPeer Create(Identity? identity = null) => new Libp2pPeer(protocolStackSettings, PeerStore, identity ?? new Identity(), identifyNotifier, activitySource, base.rootActivity, LoggerFactory);
}

class Libp2pPeer : LocalPeer
{
    public Libp2pPeer(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, Identity identity, IdentifyNotifier identifyNotifier, ActivitySource? activitySource = null, Activity? rootActivity = null, ILoggerFactory? loggerFactory = null)
        : base(identity, peerStore, protocolStackSettings, activitySource, rootActivity, loggerFactory)
    {
        identifyNotifier.TrackChanges(this);
    }

    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }
}
