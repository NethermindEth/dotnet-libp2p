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

    protected override async Task ConnectedTo(ISession peer, bool isDialer)
    {
        await peer.DialAsync<IdentifyProtocol>();
    }

    protected override IProtocol SelectProtocol(Multiaddress addr)
    {
        ITransportProtocol protocol = null!;

        if (addr.Has<QUICv1>())
        {
            protocol = TopProtocols.FirstOrDefault(proto => proto.Id == "quic-v1") as ITransportProtocol ?? throw new ApplicationException("QUICv1 is not supported");
        }
        else if (addr.Has<TCP>())
        {
            protocol = TopProtocols!.FirstOrDefault(proto => proto.Id == "ip-tcp") as ITransportProtocol ?? throw new ApplicationException("TCP is not supported");
        }
        else
        {
            throw new NotImplementedException($"No transport protocol found for the given address: {addr}");
        }

        return protocol;
    }

    public override IPeer Create(Identity? identity = null)
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
