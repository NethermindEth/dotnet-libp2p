// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Core.Discovery;

public class PeerStore
{
    ConcurrentDictionary<PeerId, PeerInfo> store = [];

    public void Discover(Multiaddress[] addrs)
    {
        if (addrs is { Length: 0 })
        {
            return;
        }

        PeerId? peerId = addrs.FirstOrDefault()?.GetPeerId();

        if (peerId is not null)
        {
            PeerInfo? newOne = null;
            PeerInfo peerInfo = store.GetOrAdd(peerId, (id) => newOne = new PeerInfo { Addrs = [.. addrs] });
            if(peerInfo != newOne && peerInfo.Addrs is not null && peerInfo.Addrs.Count == addrs.Length && addrs.All(peerInfo.Addrs.Contains))
            {
                return;
            }
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
                if(item.Addrs is not null) value.Invoke(item.Addrs.ToArray());
            }
        }
        remove
        {
            onNewPeer -= value;
        }
    }

    public override string ToString()
    {
        return $"peerStore({store.Count}):{string.Join(",", store.Select(x => x.Key.ToString() ?? "null"))}";
    }

    public PeerInfo GetPeerInfo(PeerId peerId)
    {
        return store.GetOrAdd(peerId, id => new PeerInfo());
    }

    public class PeerInfo
    {
        public ByteString? SignedPeerRecord { get; set; }
        public HashSet<Multiaddress>? Addrs { get; set; }
    }
}

