// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>(serviceProvider),
    ILibp2pPeerFactoryBuilder
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IpTcpProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(QuicProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NoiseProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(YamuxProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MultistreamProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(PingProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IdentifyPushProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IdentifyProtocol))]
    private static void PreserveTransportProtocolMetadata() { }

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
        throw new NotImplementedException("Relay protocol is not yet implemented");
        addRelay = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithQuic()
    {
        addQuic = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        // Ensure transport protocol static methods are preserved for AOT/trimming
        PreserveTransportProtocolMetadata();

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
