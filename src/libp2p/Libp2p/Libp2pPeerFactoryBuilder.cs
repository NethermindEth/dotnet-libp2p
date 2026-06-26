// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.I2p;
using Nethermind.Libp2p.Protocols.Tls;
using Nethermind.Libp2p.Protocols.WebRtc;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>(serviceProvider),
    ILibp2pPeerFactoryBuilder
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IpTcpProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(QuicProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(I2pProtocol))]
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
    private bool addI2p;
    private I2pOptions? i2pOptions;
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

    public ILibp2pPeerFactoryBuilder WithI2p(string? samHost = null, int? samPort = null, string? destinationKeyFile = null)
    {
        addI2p = true;
        i2pOptions ??= new I2pOptions { UsePrimarySessionForStreams = false };

        if (samHost is not null)
        {
            i2pOptions.SamHost = samHost;
            i2pOptions.SamUdpHost = samHost;
        }
        if (samPort is not null)
        {
            i2pOptions.SamPort = samPort.Value;
        }
        if (destinationKeyFile is not null)
        {
            i2pOptions.DestinationKeyFile = destinationKeyFile;
        }

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

    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        // Ensure transport protocol static methods are preserved for AOT/trimming
        PreserveTransportProtocolMetadata();

        ProtocolRef tcp = Get<IpTcpProtocol>();
#if LIBP2P_WEBSOCKETS
        ProtocolRef[] streamTransports = addWebSockets ? [tcp, Get<WebSocketProtocol>()] : [tcp];
#else
        ProtocolRef[] streamTransports = [tcp];
#endif
        if (addI2p)
        {
            ProtocolRef i2p = new(ActivatorUtilities.CreateInstance<I2pProtocol>(ServiceProvider, i2pOptions ?? new I2pOptions()));
            streamTransports = [.. streamTransports, i2p];
        }

        ProtocolRef[] encryption = enforcePlaintext ? [Get<PlainTextProtocol>()] : [Get<NoiseProtocol>(), Get<TlsProtocol>()];

        ProtocolRef[] muxers = [Get<YamuxProtocol>()];

        ProtocolRef[] commonAppProtocolSelector = [Get<MultistreamProtocol>()];
        Connect(streamTransports, [Get<MultistreamProtocol>()], encryption, [Get<MultistreamProtocol>()], muxers, commonAppProtocolSelector);

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

        return [.. transports];
    }
}
