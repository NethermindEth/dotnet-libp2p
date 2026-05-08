// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

public class TestPeerFactory : PeerFactory
{
    private readonly IProtocolStackSettings _protocolStackSettings;
    private readonly PeerStore _peerStore;
    private readonly ActivitySource? _activitySource;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly ConcurrentDictionary<PeerId, ILocalPeer> _peers = new();

    public TestPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ActivitySource? activitySource = null, ILoggerFactory? loggerFactory = null)
        : base(protocolStackSettings, peerStore, activitySource)
    {
        _protocolStackSettings = protocolStackSettings;
        _peerStore = peerStore;
        _activitySource = activitySource;
        _loggerFactory = loggerFactory;
    }

    public override ILocalPeer Create(Identity? identity = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return _peers.GetOrAdd(identity.PeerId, _ => new TestLocalPeer(identity, _protocolStackSettings, _peerStore, _activitySource, _loggerFactory));
    }
}

internal class TestLocalPeer : LocalPeer
{
    public TestLocalPeer(Identity id, IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ActivitySource? activitySource = null, ILoggerFactory? loggerFactory = null)
        : base(id, peerStore, protocolStackSettings, activitySource, null, loggerFactory)
    {
    }

    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }
}
