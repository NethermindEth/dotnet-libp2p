// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Builder;

public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>,
    IPeerFactoryBuilder
{
    public Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : base(serviceProvider)
    {
    }

    public static Libp2pPeerFactoryBuilder Create => new();

    protected override IPeerFactoryBuilder BuildTransportLayer()
    {
        return Over<IpTcpProtocol>()
            .Select<MultistreamProtocol>()
            //.Over<NoiseProtocol>()
// #if DEBUG
            .Over<PlainTextProtocol>()
// #endif
            .Select<MultistreamProtocol>()
            .Over<YamuxProtocol>()
            .Select<MultistreamProtocol>()
            .AddAppLayerProtocol<IpfsIdProtocol>();
    }
}
