// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Tls;

namespace Nethermind.Libp2p;

public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>,
    ILibp2pPeerFactoryBuilder
{
    public Libp2pPeerFactoryBuilder(IServiceProvider? serviceProvider = default) : base(serviceProvider)
    {
        ProtocolLimits = serviceProvider?.GetService<BuiltInProtocolLimits>() ?? new();
        if (serviceProvider?.GetService<IdentifyNotifier>() is null)
            Services.AddSingleton<IdentifyNotifier>();
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IpTcpProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(QuicProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NoiseProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(YamuxProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MultistreamProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(PingProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IdentifyPushProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IdentifyProtocol))]
    private static void PreserveTransportProtocolMetadata() { }

    /// <summary>Replace these settings before Build to configure or disable built-in protocol limits.</summary>
    public BuiltInProtocolLimits ProtocolLimits { get; set; }

    private bool enforcePlaintext;
    private bool addPubsub;
    private bool addRelay;
    private bool addQuic;

    /// <summary>
    /// Exposes the service collection for protocol integration.
    /// Protocols can register their services here before the peer is built.
    /// </summary>
    public IServiceCollection Services => InternalServices;

    public ILibp2pPeerFactoryBuilder WithPlaintextEnforced()
    {
        EnsureConfigurable();
        enforcePlaintext = true;
        return this;
    }

    /// <inheritdoc />
    public ILibp2pPeerFactoryBuilder WithPubsub()
    {
        EnsureConfigurable();
        addPubsub = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithRelay()
    {
        EnsureConfigurable();
        addRelay = true;
        return this;
    }

    public ILibp2pPeerFactoryBuilder WithQuic()
    {
        EnsureConfigurable();
        addQuic = true;
        return this;
    }

    /// <summary>The default Identify protocol, or null to omit it.</summary>
    protected virtual ProtocolRef? Identify() => Get<IdentifyProtocol>(ProtocolLimits.Identify);

    /// <summary>The default Identify Push protocol, or null to omit it.</summary>
    protected virtual ProtocolRef? IdentifyPush() => Get<IdentifyPushProtocol>(ProtocolLimits.IdentifyPush);

    /// <summary>The default Ping protocol, or null to omit it.</summary>
    protected virtual ProtocolRef? Ping() => Get<PingProtocol>(ProtocolLimits.Ping);

    /// <summary>Stream transports wired through encryption and multiplexing.</summary>
    protected virtual ProtocolRef[] StreamTransports() => [Get<IpTcpProtocol>()];

    /// <summary>Transports with built-in security and multiplexing, wired directly to applications.</summary>
    protected virtual ProtocolRef[] DatagramTransports() => addQuic ? [Get<QuicProtocol>()] : [];

    /// <summary>Security protocols offered on stream transports, in negotiation order.</summary>
    protected virtual ProtocolRef[] Encryption() => enforcePlaintext ? [Get<PlainTextProtocol>()] : [Get<NoiseProtocol>(), Get<TlsProtocol>()];

    /// <summary>Stream multiplexers, in negotiation order.</summary>
    protected virtual ProtocolRef[] Muxers() => [Get<YamuxProtocol>()];

    /// <summary>Relay protocols wired to the other application protocols.</summary>
    protected virtual ProtocolRef[] Relay() => addRelay ? [Get<RelayStopProtocol>(ProtocolLimits.RelayStop), Get<RelayHopProtocol>(ProtocolLimits.RelayHop)] : [];

    /// <summary>Pubsub application protocols, in negotiation order.</summary>
    protected virtual ProtocolRef[] Pubsub()
    {
        if (!addPubsub) return [];
        if (ServiceProvider.GetService<PubsubRouter>() is null)
        {
            throw new InvalidOperationException("WithPubsub() requires a registered PubsubRouter. Call AddPubsub() on the service collection used by this builder.");
        }

        return [
            Get<GossipsubProtocolV13>(ProtocolLimits.Pubsub),
            Get<GossipsubProtocolV12>(ProtocolLimits.Pubsub),
            Get<GossipsubProtocolV11>(ProtocolLimits.Pubsub),
            Get<GossipsubProtocol>(ProtocolLimits.Pubsub),
            Get<FloodsubProtocol>(ProtocolLimits.Pubsub)
        ];
    }

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
