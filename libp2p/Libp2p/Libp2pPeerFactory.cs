// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using System.Diagnostics;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactory(
        IProtocolStackSettings protocolStackSettings,
        PeerStore peerStore,
        IdentifyNotifier identifyNotifier,
        ActivitySource? activitySource = null,
        Activity? rootActivity = null,
        ILoggerFactory? loggerFactory = null, PeerResourceLimits? resourceLimits = null, TimeProvider? timeProvider = null)
    : PeerFactory(protocolStackSettings, peerStore, activitySource, rootActivity, loggerFactory, resourceLimits, timeProvider)
{
    public Libp2pPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier,
        ActivitySource? activitySource, Activity? rootActivity, ILoggerFactory? loggerFactory)
        : this(protocolStackSettings, peerStore, identifyNotifier, activitySource, rootActivity, loggerFactory, null, null) { }

    public override ILocalPeer Create(Identity? identity = null) => new Libp2pPeer(protocolStackSettings, PeerStore, identity ?? new Identity(), identifyNotifier, activitySource, base.rootActivity, LoggerFactory, ResourceLimits, Clock);
}

public class Libp2pPeer : LocalPeer
{
    public Libp2pPeer(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, Identity identity, IdentifyNotifier identifyNotifier, ActivitySource? activitySource = null, Activity? rootActivity = null, ILoggerFactory? loggerFactory = null, PeerResourceLimits? resourceLimits = null, TimeProvider? timeProvider = null)
        : base(identity, peerStore, protocolStackSettings, activitySource, rootActivity, loggerFactory, resourceLimits: resourceLimits, timeProvider: timeProvider)
    {
        identifyNotifier.TrackChanges(this);
    }

    public Libp2pPeer(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, Identity identity, IdentifyNotifier identifyNotifier,
        ActivitySource? activitySource, Activity? rootActivity, ILoggerFactory? loggerFactory)
        : this(protocolStackSettings, peerStore, identity, identifyNotifier, activitySource, rootActivity, loggerFactory, null, null) { }

    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        if (GetProtocol<ISessionProtocol>(IdentifyProtocol.ProtocolId) is { } identify)
        {
            await session.DialAsync(identify, session.Closed);
        }
    }
}
