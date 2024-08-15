// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using System.Runtime.Versioning;

namespace Nethermind.Libp2p.Stack;

[RequiresPreviewFeatures]
public class Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>(serviceProvider),
    ILibp2pPeerFactoryBuilder
{
    private bool enforcePlaintext;
    private bool addPubsub;

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced()
    {
        enforcePlaintext = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithPubsub()
    {
        addPubsub = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(ProtocolRef[] additionalProtocols)
    {
        ProtocolRef[] transports = [
            Get<IpTcpProtocol>(),
            Get<QuicProtocol>()
            ];

        ProtocolRef[] selector1 = [Get<MultistreamProtocol>()];
        Connect(transports, selector1);

        ProtocolRef[] encryption = [enforcePlaintext ?
            Get<PlainTextProtocol>() :
            Get<NoiseProtocol>()];
        Connect(selector1, encryption);

        ProtocolRef[] selector2 = [Get<MultistreamProtocol>()];
        Connect(encryption, selector2);

        ProtocolRef[] muxers = [Get<YamuxProtocol>()];
        Connect(selector2, muxers);

        ProtocolRef[] selector3 = [Get<MultistreamProtocol>()];
        Connect(muxers, selector3);

        ProtocolRef relay = Get<RelayProtocol>();
        ProtocolRef[] pubsub = addPubsub ? [
            Get<GossipsubProtocolV12>(),
            Get<GossipsubProtocolV11>(),
            Get<GossipsubProtocol>(),
            Get<FloodsubProtocol>()
            ] : [];

        ProtocolRef[] apps = [
            Get<IdentifyProtocol>(),
            .. additionalProtocols,
            relay,
            .. pubsub,
        ];
        Connect(selector3, apps);

        ProtocolRef[] relaySelector = [Get<MultistreamProtocol>()];
        Connect([relay], relaySelector);
        Connect(relaySelector, apps.Where(a => a != relay).ToArray());

        return transports;
    }
}
