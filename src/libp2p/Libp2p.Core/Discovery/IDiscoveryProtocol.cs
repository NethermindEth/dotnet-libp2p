// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Discovery;

public interface IDiscoveryProtocol
{
    Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default);
    Func<Multiaddress[], bool>? OnAddPeer { set; }
    Func<Multiaddress[], bool>? OnRemovePeer { set; }
}
