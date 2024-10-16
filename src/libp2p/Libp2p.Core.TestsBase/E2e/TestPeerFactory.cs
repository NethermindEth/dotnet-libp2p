// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

internal class TestPeerFactory(IServiceProvider serviceProvider) : PeerFactory(serviceProvider)
{
    ConcurrentDictionary<PeerId, ILocalPeer> peers = new();

    public override ILocalPeer Create(Identity? identity = null, Multiaddress? localAddr = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return peers.GetOrAdd(identity.PeerId, (p) => new TestLocalPeer(identity));
    }
}
