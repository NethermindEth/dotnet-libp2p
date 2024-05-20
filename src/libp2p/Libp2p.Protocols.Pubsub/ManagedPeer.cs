// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Libp2p.Protocols.Pubsub;
internal class ManagedPeer
{
    private ILocalPeer peer;

    public ManagedPeer(ILocalPeer peer)
    {
        this.peer = peer;
    }

    internal async Task<IRemotePeer> DialAsync(Multiaddress[] addrs, CancellationToken token)
    {
        Dictionary<Multiaddress, CancellationTokenSource> cancellations = new();
        foreach (Multiaddress addr in addrs)
        {
            cancellations[addr] = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        IRemotePeer firstConnected = (await Task.WhenAny(addrs
            .Select(addr => peer.DialAsync(addr, cancellations[addr].Token)))).Result;

        foreach (KeyValuePair<Multiaddress, CancellationTokenSource> c in cancellations)
        {
            if (c.Key != firstConnected.Address)
            {
                c.Value.Cancel(false);
            }
        }

        return firstConnected;
    }
}
