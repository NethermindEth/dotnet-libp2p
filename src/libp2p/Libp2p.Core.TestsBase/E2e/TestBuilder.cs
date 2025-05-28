// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class TestBuilder(IServiceProvider? serviceProvider = null) : PeerFactoryBuilderBase<TestBuilder, TestPeerFactory>(serviceProvider)
{
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef root = Get<TestMuxerProtocol>();

        Connect([root],
            [
                Get<TestPingProtocol>(),
                Get<IdentifyProtocol>(),
                .. additionalProtocols
            ]);

        return [root];
    }
}

public class TestPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ActivitySource? activitySource = null, ILoggerFactory? loggerFactory = null) : PeerFactory(protocolStackSettings, peerStore, activitySource)
{
    readonly ConcurrentDictionary<PeerId, ILocalPeer> peers = new();

    public override ILocalPeer Create(Identity? identity = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return peers.GetOrAdd(identity.PeerId, (p) => new TestLocalPeer(identity, protocolStackSettings, base.PeerStore, activitySource, loggerFactory));
    }
}

internal class TestLocalPeer(Identity id, IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ActivitySource? activitySource = null, ILoggerFactory? loggerFactory = null) : LocalPeer(id, peerStore, protocolStackSettings, activitySource, null, loggerFactory)
{
    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }
}
