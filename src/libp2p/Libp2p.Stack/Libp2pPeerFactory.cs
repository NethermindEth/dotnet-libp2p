// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Stack;

public class Libp2pPeerFactory : PeerFactory
{
    public Libp2pPeerFactory(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    protected override async Task ConnectedTo(IRemotePeer peer, bool isDialer)
    {
        await peer.DialAsync<IpfsIdProtocol>();
    }
}
