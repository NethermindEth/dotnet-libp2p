// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:MIT

using Nethermind.Libp2p.Builder;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Chat;

public class NoStackPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>,
    IPeerFactoryBuilder
{
    public NoStackPeerFactoryBuilder() : base(default)
    {
    }

    public static Libp2pPeerFactoryBuilder Create => new();

    protected override IPeerFactoryBuilder BuildTransportLayer()
    {
        return Over<IpTcpProtocol>();
    }
}
