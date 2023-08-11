// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p.Builder;

public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>,
    ILibp2pPeerFactoryBuilder
{
    private bool enforcePlaintext;

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced()
    {
        enforcePlaintext = true;
        return this;
    }

    public Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : base(serviceProvider)
    {
    }

    public static Libp2pPeerFactoryBuilder Create => new();

    protected override ProtocolStack BuildStack()
    {
        ProtocolStack tcpEncryptionStack = enforcePlaintext ?
            Over<PlainTextProtocol>() :
            Over<NoiseProtocol>();

        ProtocolStack tcpStack =
            Over<IpTcpProtocol>()
            .Over<MultistreamProtocol>()
            .Over(tcpEncryptionStack)
            .Over<MultistreamProtocol>()
            .Over<YamuxProtocol>()
            .Over<MultistreamProtocol>();

        ProtocolStack quicStack =
            Over<QuicProtocol>();

        return
            Over<MultiAddrBasedSelectorProtocol>()
            .Over(quicStack).Or(tcpStack)
            .AddAppLayerProtocol<IpfsIdProtocol>()
            //.AddAppLayerProtocol<GossipsubProtocolV11>()
            //.AddAppLayerProtocol<GossipsubProtocol>()
            .AddAppLayerProtocol<FloodsubProtocol>();
    }
}
