// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;
using Nethermind.Libp2p.Protocols.WebRtc;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>(serviceProvider),
    ILibp2pPeerFactoryBuilder
{
    private bool enforcePlaintext;
    private bool addPubsub;
    private bool addRelay;
    private bool addQuic;
    private bool addWebRtcDirect;

    /// <summary>
    /// Exposes the service collection for protocol integration.
    /// Protocols can register their services here before the peer is built.
    /// </summary>
    public IServiceCollection Services => InternalServices;

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

    public ILibp2pPeerFactoryBuilder WithQuic()
    {
        addQuic = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithWebRtcDirect()
    {
        addWebRtcDirect = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();

        ProtocolRef[] encryption = enforcePlaintext ? [Get<PlainTextProtocol>()] : [Get<NoiseProtocol>(), Get<TlsProtocol>()];

        ProtocolRef[] muxers = [Get<YamuxProtocol>()];

        ProtocolRef[] commonAppProtocolSelector = [Get<MultistreamProtocol>()];
        Connect([tcp], [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonAppProtocolSelector);

        ProtocolRef[] relay = addRelay ? [Get<RelayStopProtocol>(), Get<RelayHopProtocol>()] : [];
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

        List<ProtocolRef> transports = [tcp];

        if (addQuic)
        {
            ProtocolRef quic = Get<QuicProtocol>();
            Connect([quic], commonAppProtocolSelector);
            transports.Add(quic);
        }

        if (addWebRtcDirect)
        {
            ProtocolRef webrtcDirect = Get<WebRtcDirectProtocol>();
            Connect([webrtcDirect], commonAppProtocolSelector);
            transports.Add(webrtcDirect);
        }

        return [.. transports];
    }
}
