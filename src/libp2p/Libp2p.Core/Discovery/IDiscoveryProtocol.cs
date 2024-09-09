// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Discovery;

public interface IDiscoveryProtocol
{
    Task DiscoverAsync(Multiaddress localPeerAddr, CancellationToken token = default);
}


public class PeerStore
{
    List<Multiaddress[]> store = [];

    public void Discover(Multiaddress[] addrs)
    {
        store.Add(addrs);
        OnNewPeer?.Invoke(addrs);
    }

    public event Action<Multiaddress[]>? OnNewPeer;

    public override string ToString()
    {
        return $"peerStore({store.Count}):{string.Join(",", store.Select(x=>x.FirstOrDefault()?.GetPeerId()?.ToString() ?? "null"))})";
    }
}
