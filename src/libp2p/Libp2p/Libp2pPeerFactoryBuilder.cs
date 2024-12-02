// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Libp2p.Stack;

[RequiresPreviewFeatures]
public class Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>(serviceProvider),
    ILibp2pPeerFactoryBuilder
{
    private bool enforcePlaintext;
    private bool addPubsub;
    private bool addRelay;

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

    public ILibp2pPeerFactoryBuilder WithRelay()
    {
        addRelay = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(ProtocolRef[] additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();

        ProtocolRef[] encryption = [enforcePlaintext ?
            Get<PlainTextProtocol>() :
            Get<NoiseProtocol>()];

        ProtocolRef[] muxers = [Get<YamuxProtocol>()];

        ProtocolRef[] commonSelector = [Get<MultistreamProtocol>()];
        Connect([tcp], [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonSelector);

        ProtocolRef quic = Get<QuicProtocol>();
        Connect([quic], commonSelector);

        ProtocolRef[] relay = addRelay ?  [Get<RelayHopProtocol>(), Get<RelayHopProtocol>()] : [];
        ProtocolRef[] pubsub = addPubsub ? [
            Get<GossipsubProtocolV12>(),
            Get<GossipsubProtocolV11>(),
            Get<GossipsubProtocol>(),
            Get<FloodsubProtocol>()
            ] : [];

        ProtocolRef[] apps = [
            Get<IdentifyProtocol>(),
            .. additionalProtocols,
            .. relay,
            .. pubsub,
        ];
        Connect(commonSelector, apps);

        if (addRelay)
        {
            ProtocolRef[] relaySelector = [Get<MultistreamProtocol>()];
            Connect(relay, relaySelector);
            Connect(relaySelector, apps.Where(a => !relay.Contains(a)).ToArray());
        }

        return [tcp, quic];
    }
}
