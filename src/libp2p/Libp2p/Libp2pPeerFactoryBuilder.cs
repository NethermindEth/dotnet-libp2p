// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Runtime.Versioning;

namespace Nethermind.Libp2p.Stack;

[RequiresPreviewFeatures]
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
            .Over<YamuxProtocol>();

        ProtocolStack relayStack = Over<MultistreamProtocol>().Over<NoiseProtocol>();

        return
            Over<QuicProtocol>().Or(tcpStack).Or(relayStack)
            .Over<MultistreamProtocol>()
            .AddAppLayerProtocol<IdentifyProtocol>()
            //.AddAppLayerProtocol<GossipsubProtocolV12>()
            //.AddAppLayerProtocol<GossipsubProtocolV11>()
            .AddAppLayerProtocol<GossipsubProtocol>()
            .AddAppLayerProtocol<FloodsubProtocol>();
    }
}
