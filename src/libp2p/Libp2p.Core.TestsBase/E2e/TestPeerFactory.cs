// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Stack;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

internal class TestPeerFactory(IProtocolStackSettings protocolStackSettings) : PeerFactory(protocolStackSettings)
{
    ConcurrentDictionary<PeerId, IPeer> peers = new();

    public override IPeer Create(Identity? identity = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return peers.GetOrAdd(identity.PeerId, (p) => new TestLocalPeer(identity));
    }
}
