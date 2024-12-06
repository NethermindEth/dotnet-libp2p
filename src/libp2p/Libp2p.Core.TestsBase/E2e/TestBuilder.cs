// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Stack;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class TestBuilder(IServiceProvider? serviceProvider = null) : PeerFactoryBuilderBase<TestBuilder, TestPeerFactory>(serviceProvider)
{
    protected override ProtocolRef[] BuildStack(ProtocolRef[] additionalProtocols)
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

public class TestPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ILoggerFactory? loggerFactory = null) : PeerFactory(protocolStackSettings, peerStore)
{
    ConcurrentDictionary<PeerId, IPeer> peers = new();

    public override IPeer Create(Identity? identity = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return peers.GetOrAdd(identity.PeerId, (p) => new TestLocalPeer(identity, protocolStackSettings, peerStore, loggerFactory));
    }
}

internal class TestLocalPeer(Identity id, IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ILoggerFactory? loggerFactory = null) : LocalPeer(id, peerStore, protocolStackSettings, loggerFactory)
{
    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        try
        {
            await session.DialAsync<IdentifyProtocol>();
        }
        catch
        {

        }
    }
}
