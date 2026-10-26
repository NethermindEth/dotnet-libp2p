// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Tls;
using Nethermind.Libp2p.Protocols.WebRtc;

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
#if LIBP2P_WEBSOCKETS
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(WebSocketProtocol))]
#endif
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(WebRtcDirectProtocol))]
    private static void PreserveTransportProtocolMetadata() { }

    private bool enforcePlaintext;
    private bool addPubsub;
    private bool addRelay;
    private bool addQuic;
#if LIBP2P_WEBSOCKETS
    private bool addWebSockets;
#endif
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
#if LIBP2P_WEBSOCKETS
        addWebSockets = true;
        return this;
#else
        throw new PlatformNotSupportedException("WebSockets transport is not available on this target runtime.");
#endif
    }

    public ILibp2pPeerFactoryBuilder WithWebRtcDirect()
    {
        addWebRtcDirect = true;
        return this;
    }

    /// <summary>The default Identify protocol, or null to omit it.</summary>
    protected virtual ProtocolRef? Identify() => Get<IdentifyProtocol>();

    /// <summary>The default Identify Push protocol, or null to omit it.</summary>
    protected virtual ProtocolRef? IdentifyPush() => Get<IdentifyPushProtocol>();

    /// <summary>The default Ping protocol, or null to omit it.</summary>
    protected virtual ProtocolRef? Ping() => Get<PingProtocol>();

    /// <summary>Stream transports wired through encryption and multiplexing.</summary>
    protected virtual ProtocolRef[] StreamTransports()
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();
#if LIBP2P_WEBSOCKETS
        return addWebSockets ? [tcp, Get<WebSocketProtocol>()] : [tcp];
#else
        return [tcp];
#endif
    }

    /// <summary>Transports with built-in security and multiplexing, wired directly to applications.</summary>
    protected virtual ProtocolRef[] DatagramTransports()
    {
        List<ProtocolRef> transports = [];
        if (addQuic) transports.Add(Get<QuicProtocol>());
        if (addWebRtcDirect) transports.Add(Get<WebRtcDirectProtocol>());
        return [.. transports];
    }

    /// <summary>Security protocols offered on stream transports, in negotiation order.</summary>
    protected virtual ProtocolRef[] Encryption() => enforcePlaintext ? [Get<PlainTextProtocol>()] : [Get<NoiseProtocol>(), Get<TlsProtocol>()];

    /// <summary>Stream multiplexers, in negotiation order.</summary>
    protected virtual ProtocolRef[] Muxers() => [Get<YamuxProtocol>()];

    /// <summary>Relay protocols wired to the other application protocols.</summary>
    protected virtual ProtocolRef[] Relay() => addRelay ? [Get<RelayStopProtocol>(), Get<RelayHopProtocol>()] : [];

    /// <summary>Pubsub application protocols, in negotiation order.</summary>
    protected virtual ProtocolRef[] Pubsub() => addPubsub ? [
        Get<GossipsubProtocolV13>(),
        Get<GossipsubProtocolV12>(),
        Get<GossipsubProtocolV11>(),
        Get<GossipsubProtocol>(),
        Get<FloodsubProtocol>()
        ] : [];

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        // Ensure transport protocol static methods are preserved for AOT/trimming
        PreserveTransportProtocolMetadata();

        ProtocolRef[] streamTransports = StreamTransports();
        ProtocolRef[] encryption = Encryption();
        ProtocolRef[] muxers = Muxers();

        ProtocolRef[] commonAppProtocolSelector = [Get<MultistreamProtocol>()];
        Connect(streamTransports, [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonAppProtocolSelector);

        ProtocolRef[] relay = Relay();
        ProtocolRef[] pubsub = Pubsub();
        ProtocolRef?[] core = [Identify(), IdentifyPush(), Ping()];

        ProtocolRef[] apps = [
            .. core.OfType<ProtocolRef>(),
            .. additionalProtocols,
            .. relay,
            .. pubsub,
        ];
        Connect(commonAppProtocolSelector, apps);

        if (relay.Length > 0)
        {
            Connect(relay, [Get<MultistreamProtocol>()], apps.Where(a => !relay.Contains(a)).ToArray());
        }

        ProtocolRef[] datagramTransports = DatagramTransports();
        Connect(datagramTransports, commonAppProtocolSelector);
        return [.. streamTransports, .. datagramTransports];
    }
}
