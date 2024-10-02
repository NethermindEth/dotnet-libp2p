// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.Discovery;

public class PeerStore
{
    ConcurrentDictionary<PeerId, Multiaddress[]> store = [];

    public void Discover(Multiaddress[] addrs)
    {
        if (addrs is { Length: 0 })
        {
            return;
        }

        PeerId? peerId = addrs.FirstOrDefault()?.GetPeerId();

        if (peerId is not null && store.TryAdd(peerId, addrs))
        {
            onNewPeer?.Invoke(addrs);
        }
    }

    private Action<Multiaddress[]>? onNewPeer = null;

    public event Action<Multiaddress[]>? OnNewPeer
    {
        add
        {
            if (value is null)
            {
                return;
            }

            onNewPeer += value;
            foreach (var item in store.Select(x => x.Value).ToArray())
            {
                value.Invoke(item);
            }
        }
        remove
        {
            onNewPeer -= value;
        }
    }

    public override string ToString()
    {
        return $"peerStore({store.Count}):{string.Join(",", store.Select(x => x.Key.ToString() ?? "null"))})";
    }
}
