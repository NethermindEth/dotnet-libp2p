// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
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
        await peer.DialAsync<IdentifyProtocol>();
    }

    public override ILocalPeer Create(Identity? identity = null, Multiaddress? localAddr = null)
    {
        identity ??= new Identity();
        localAddr ??= $"/ip4/0.0.0.0/tcp/0/p2p/{identity.PeerId}";
        if (localAddr.Get<P2P>() is null)
        {
            localAddr.Add<P2P>(identity.PeerId.ToString());
        }

        return new LocalPeer(this) { Identity = identity ?? new Identity(), Address = localAddr ?? $"/ip4/0.0.0.0/tcp/0/p2p/{identity.PeerId}" };
    }
}
