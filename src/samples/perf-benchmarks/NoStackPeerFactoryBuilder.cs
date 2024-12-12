// SPDX-FileCopyrightText:2023 Demerzel Solutions Limited
// SPDX-License-Identifier:MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p;

namespace DataTransferBenchmark;

public class NoStackPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>,
    IPeerFactoryBuilder
{
    public NoStackPeerFactoryBuilder() : base(default)
    {
    }

    public static Libp2pPeerFactoryBuilder Create => new();

    protected override ProtocolRef[] BuildStack(ProtocolRef[] additionalProtocols)
    {
        return [Get<IpTcpProtocol>()];
    }
}
