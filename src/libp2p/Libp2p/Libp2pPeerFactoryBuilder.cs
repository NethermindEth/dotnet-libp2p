// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>(serviceProvider),
    ILibp2pPeerFactoryBuilder
{
    private bool enforcePlaintext;
    private bool addPubsub;
    private bool addRelay;
    private bool addQuic;

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
        //addRelay = true;
        //return this;
        throw new NotImplementedException("Relay protocol is not yet implemented");
    }

    public ILibp2pPeerFactoryBuilder WithQuic()
    {
        addQuic = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();

        ProtocolRef[] encryption = enforcePlaintext ? [Get<PlainTextProtocol>()] : [Get<NoiseProtocol>()/*, Get<TlsProtocol>()*/];

        ProtocolRef[] muxers = [Get<YamuxProtocol>()];

        ProtocolRef[] commonAppProtocolSelector = [Get<MultistreamProtocol>()];
        Connect([tcp], [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonAppProtocolSelector);

        ProtocolRef[] relay = addRelay ? [Get<RelayHopProtocol>(), Get<RelayStopProtocol>()] : [];
        ProtocolRef[] pubsub = addPubsub ? [
            Get<GossipsubProtocolV12>(),
            Get<GossipsubProtocolV11>(),
            Get<GossipsubProtocol>(),
            Get<FloodsubProtocol>()
            ] : [];

        ProtocolRef[] apps = [
            Get<IdentifyProtocol>(),
            Get<IdentifyPushProtocol>(),
            Get<PingProtocol>(),
            .. additionalProtocols,
            .. relay,
            .. pubsub,
        ];
        Connect(commonAppProtocolSelector, apps);

        if (addRelay)
        {
            Connect(relay, [Get<MultistreamProtocol>()], apps.Where(a => !relay.Contains(a)).ToArray());
        }

        if (addQuic)
        {
            ProtocolRef quic = Get<QuicProtocol>();
            Connect([quic], commonAppProtocolSelector);
            return [tcp, quic];
        }

        return [tcp];
    }
}
