// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Stack;

public class Libp2pPeerFactory(IProtocolStackSettings protocolStackSettings) : PeerFactory(protocolStackSettings)
{
    public override IPeer Create(Identity? identity = null) => new Libp2pPeer(protocolStackSettings, identity ?? new Identity());
}

class Libp2pPeer(IProtocolStackSettings protocolStackSettings, Identity identity) : LocalPeer(protocolStackSettings, identity)
{
    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }

    protected override IProtocol SelectProtocol(Multiaddress addr)
    {
        ArgumentNullException.ThrowIfNull(protocolStackSettings.TopProtocols);

        ITransportProtocol protocol = null!;

        if (addr.Has<QUICv1>())
        {
            protocol = protocolStackSettings.TopProtocols.FirstOrDefault(proto => proto.Id == "quic-v1") as ITransportProtocol ?? throw new ApplicationException("QUICv1 is not supported");
        }
        else if (addr.Has<TCP>())
        {
            protocol = protocolStackSettings.TopProtocols!.FirstOrDefault(proto => proto.Id == "ip-tcp") as ITransportProtocol ?? throw new ApplicationException("TCP is not supported");
        }
        else
        {
            throw new NotImplementedException($"No transport protocol found for the given address: {addr}");
        }

        return protocol;
    }
}
