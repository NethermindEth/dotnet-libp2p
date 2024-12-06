// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Stack;

public class Libp2pPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, ILoggerFactory? loggerFactory = null) : PeerFactory(protocolStackSettings, peerStore, loggerFactory)
{
    public override IPeer Create(Identity? identity = null) => new Libp2pPeer(protocolStackSettings, peerStore, identity ?? new Identity(), loggerFactory);
}

class Libp2pPeer(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, Identity identity, ILoggerFactory? loggerFactory = null) : LocalPeer(identity, peerStore, protocolStackSettings, loggerFactory)
{
    protected override async Task ConnectedTo(ISession session, bool isDialer)
    {
        await session.DialAsync<IdentifyProtocol>();
    }

    protected override ProtocolRef SelectProtocol(Multiaddress addr)
    {
        ArgumentNullException.ThrowIfNull(_protocolStackSettings.TopProtocols);

        ProtocolRef? protocol;

        if (addr.Has<QUICv1>())
        {
            protocol = _protocolStackSettings.TopProtocols.FirstOrDefault(proto => proto.Protocol.Id == "quic-v1") ?? throw new ApplicationException("QUICv1 is not supported");
        }
        else if (addr.Has<TCP>())
        {
            protocol = _protocolStackSettings.TopProtocols!.FirstOrDefault(proto => proto.Protocol.Id == "ip-tcp") ?? throw new ApplicationException("TCP is not supported");
        }
        else
        {
            throw new NotImplementedException($"No transport protocol found for the given address: {addr}");
        }

        return protocol;
    }
}
