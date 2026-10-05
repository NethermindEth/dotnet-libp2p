// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
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
    private bool addWebSockets;
    private bool addWebRtc;
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

    public ILibp2pPeerFactoryBuilder WithWebSockets()
    {
        addWebSockets = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithWebRtc()
    {
        addWebRtc = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithWebRtcDirect()
    {
        addWebRtcDirect = true;
        return this;
    }

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        List<ProtocolRef> streamTransports = [];
        if (!OperatingSystem.IsBrowser())
        {
            streamTransports.Add(Get<IpTcpProtocol>());
        }
        if (addWebSockets)
        {
            streamTransports.Add(Get<WebSocketProtocol>());
        }

        ProtocolRef[] encryption = enforcePlaintext ? [Get<PlainTextProtocol>()] : [Get<NoiseProtocol>(), Get<TlsProtocol>()];

        ProtocolRef[] muxers = [Get<YamuxProtocol>()];

        ProtocolRef[] commonAppProtocolSelector = [Get<MultistreamProtocol>()];
        ProtocolRef? relayStop = addRelay ? Get<RelayStopProtocol>() : null;
        ProtocolRef? relayHop = addRelay ? Get<RelayHopProtocol>() : null;
        ProtocolRef? relayCircuitTransport = addRelay ? Get<RelayCircuitTransportProtocol>() : null;
        ProtocolRef[] endToEndTransports = relayCircuitTransport is null ? [.. streamTransports] : [.. streamTransports, relayCircuitTransport];
        if (endToEndTransports.Length > 0)
        {
            Connect(endToEndTransports, [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonAppProtocolSelector);
        }
        if (relayStop is not null)
        {
            Connect([relayStop], [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonAppProtocolSelector);
        }

        ProtocolRef[] relay = addRelay ? [relayStop!, relayHop!] : [];
        ProtocolRef[] webrtcSignaling = addWebRtc ? [Get<WebRtcSignalingProtocol>()] : [];
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
            .. webrtcSignaling,
            .. pubsub,
        ];
        Connect(commonAppProtocolSelector, apps);

        List<ProtocolRef> transports = [.. streamTransports];

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

        if (addWebRtc)
        {
            ProtocolRef webrtc = Get<WebRtcProtocol>();
            Connect([webrtc], commonAppProtocolSelector);
            transports.Add(webrtc);
        }

        if (addRelay)
        {
            transports.Add(relayCircuitTransport!);
        }

        if (transports.Count is 0)
        {
            throw new Libp2pSetupException("No browser-compatible transport was configured. Use WithWebSockets(), WithWebRtc(), or WithWebRtcDirect() for browser peers.");
        }

        return [.. transports];
    }
}
