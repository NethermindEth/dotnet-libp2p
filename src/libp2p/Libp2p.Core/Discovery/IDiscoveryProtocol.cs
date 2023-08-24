// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core.Discovery;

public interface IDiscoveryProtocol
{
    Task DiscoverAsync(Multiaddr localPeerAddr, CancellationToken token = default);
    Task BanPeer();

    Func<Multiaddr[], bool>? OnAddPeer { set; }
    Func<Multiaddr[], bool>? OnRemovePeer { set; }
}
