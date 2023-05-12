// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Discovery;

public interface IDiscoveryProtocol
{
    Task DiscoverAsync(MultiAddr localPeerAddr, CancellationToken token = default);
    Task BanPeer();

    Func<MultiAddr[], bool>? OnAddPeer { set; }
    Func<MultiAddr[], bool>? OnRemovePeer { set; }
}
