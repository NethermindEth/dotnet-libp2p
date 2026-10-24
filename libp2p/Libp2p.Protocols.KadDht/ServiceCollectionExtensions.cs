// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nethermind.Libp2p.Protocols;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Kademlia;
using Multiformats.Address;
using Multiformats.Address.Protocols;

namespace Libp2p.Protocols.KadDht;

public static class ServiceCollectionExtensions
{
    public const string ProtocolId = KadDhtOptions.DefaultProtocolId;

    public static IServiceCollection AddKadDht(this IServiceCollection services,
        Action<KadDhtOptions>? configureOptions = null)
    {
        RegisterDefaults(services, configureOptions);
        services.TryAddSingleton<SharedDhtState>(sp =>
        {
            var peer = sp.GetService<ILocalPeer>();
            return (peer as IPeerServices)?.GetPeerService<DhtRuntime>()?.State ??
                new SharedDhtState(sp.GetRequiredService<IValueStore>(), sp.GetService<ILoggerFactory>(), sp.GetRequiredService<KadDhtOptions>().RecordTtl)
                { KValue = sp.GetRequiredService<KadDhtOptions>().KSize };
        });
        services.TryAddSingleton<Integration.LibP2pKademliaMessageSender>(sp =>
        {
            var peer = sp.GetRequiredService<ILocalPeer>();
            if ((peer as IPeerServices)?.GetPeerService<DhtRuntime>() is { } runtime) return runtime.Sender;
            var state = sp.GetRequiredService<SharedDhtState>();
            var peerStore = sp.GetService<PeerStore>();
            return new Integration.LibP2pKademliaMessageSender(sp.GetRequiredService<KadDhtOptions>(), peer, sp.GetService<ILoggerFactory>(),
                onPeerDiscovered: node => StorePeerAddresses(node, peerStore), onPingResponse: node => state.AddNodeCallback?.Invoke(node));
        });
        services.TryAddSingleton<DhtRuntime>(sp =>
        {
            var peer = sp.GetRequiredService<ILocalPeer>();
            return (peer as IPeerServices)?.GetPeerService<DhtRuntime>() ??
                DhtRuntime.Create(peer, sp, state: sp.GetRequiredService<SharedDhtState>(), sender: sp.GetRequiredService<Integration.LibP2pKademliaMessageSender>());
        });
        services.TryAddSingleton<IDhtMessageSender>(sp => sp.GetRequiredService<Integration.LibP2pKademliaMessageSender>());
        services.TryAddSingleton<Nethermind.Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>(sp => sp.GetRequiredService<Integration.LibP2pKademliaMessageSender>());
        services.TryAddSingleton<KadDhtProtocol>(sp => sp.GetRequiredService<DhtRuntime>().Protocol);
        services.TryAddSingleton<DhtClient>(sp => sp.GetRequiredService<DhtRuntime>().Client);
        return services;
    }

    internal static void RegisterDefaults(IServiceCollection services, Action<KadDhtOptions>? configure = null,
        IServiceProvider? fallback = null, IValueStore? valueStore = null, IProviderStore? providerStore = null)
    {
        var options = services.LastOrDefault(d => d.ServiceType == typeof(KadDhtOptions))?.ImplementationInstance as KadDhtOptions
            ?? fallback?.GetService<KadDhtOptions>() ?? new KadDhtOptions();
        configure?.Invoke(options);
        WirePeerPolicy.Validate(options);
        options.ValidateStreamLimits();
        if (configure is not null) services.AddSingleton(options);
        else services.TryAddSingleton(options);
        if (!services.Any(d => d.ServiceType == typeof(IRecordValidator)))
        {
            var suppliedValidator = fallback?.GetService<IRecordValidator>();
            if (suppliedValidator is not null) services.AddSingleton(suppliedValidator);
            else services.AddSingleton<IRecordValidator>(_ => CompositeRecordValidator.CreateDefault());
        }
        if (valueStore is not null) services.AddSingleton(valueStore);
        else if (!services.Any(d => d.ServiceType == typeof(IValueStore)))
        {
            var suppliedValues = fallback?.GetService<IValueStore>();
            if (suppliedValues is not null) services.AddSingleton(suppliedValues);
            else services.AddSingleton<IValueStore>(sp =>
                new InMemoryValueStore(sp.GetRequiredService<KadDhtOptions>().MaxStoredValues, sp.GetService<ILoggerFactory>()));
        }
        if (providerStore is not null) services.AddSingleton(providerStore);
        else if (!services.Any(d => d.ServiceType == typeof(IProviderStore)))
        {
            var suppliedProviders = fallback?.GetService<IProviderStore>();
            if (suppliedProviders is not null) services.AddSingleton(suppliedProviders);
            else services.AddSingleton<IProviderStore>(sp =>
            {
                var configured = sp.GetRequiredService<KadDhtOptions>();
                return new InMemoryProviderStore(configured.MaxProvidersPerKey, configured.MaxProviderKeys,
                    configured.MaxAddressesPerProvider, configured.MaxProviderRecordBytes, sp.GetService<ILoggerFactory>());
            });
        }
    }

    public static ILibp2pPeerFactoryBuilder WithKadDht(this ILibp2pPeerFactoryBuilder builder)
    {
        ConfigureBuilder(builder);
        return builder;
    }

    internal static void ConfigureBuilder(ILibp2pPeerFactoryBuilder builder, Action<KadDhtOptions>? configure = null,
        IEnumerable<DhtNode>? bootstrapNodes = null, IValueStore? valueStore = null, IProviderStore? providerStore = null)
    {
        RegisterDefaults(builder.Services, configure, builder.ServiceProvider, valueStore, providerStore);
        if (builder is IPeerServiceBuilder peerServices && builder is IProtocolFactoryBuilder protocols)
        {
            peerServices.AddPeerService<DhtRuntime>((peer, sp) => DhtRuntime.Create(peer, sp, bootstrapNodes));
            peerServices.AddPeerService<KadDhtProtocol>((peer, _) => ((IPeerServices)peer).GetPeerService<DhtRuntime>()!.Protocol, ownsInstance: false);
            peerServices.AddPeerService<DhtClient>((peer, _) => ((IPeerServices)peer).GetPeerService<DhtRuntime>()!.Client, ownsInstance: false);
            protocols.AddProtocolFactory(sp =>
            {
                var options = sp.GetRequiredService<KadDhtOptions>();
                var handler = KadDhtProtocolExtensions.CreateProtocol(options, ctx =>
                {
                    if (ctx.Peer is not IPeerServices services)
                        throw new InvalidOperationException("The local peer does not support peer services.");
                    return services.GetPeerService<DhtRuntime>()!.Handler;
                }, sp.GetService<ILoggerFactory>(), timeProvider: sp.GetService<TimeProvider>());
                return new ProtocolRef(handler, options.Mode == KadDhtMode.Server,
                    new InboundStreamLimit(options.MaxInboundStreams, options.MaxInboundStreamsPerPeer))
                { AcceptInbound = options.Mode == KadDhtMode.Server };
            });
        }
        else
        {
            // Custom builders may install request handlers without configuring a high-level local peer.
            var sp = builder.ServiceProvider;
            var options = sp.GetService<KadDhtOptions>() ?? builder.Services
                .Where(d => d.ServiceType == typeof(KadDhtOptions)).Select(d => d.ImplementationInstance).OfType<KadDhtOptions>().Last();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var values = valueStore ?? sp.GetService<IValueStore>() ?? new InMemoryValueStore(options.MaxStoredValues, loggerFactory);
            var providers = providerStore ?? sp.GetService<IProviderStore>() ?? new InMemoryProviderStore(options.MaxProvidersPerKey,
                options.MaxProviderKeys, options.MaxAddressesPerProvider, options.MaxProviderRecordBytes, loggerFactory);
            var validator = sp.GetService<IRecordValidator>() ?? CompositeRecordValidator.CreateDefault();
            var state = sp.GetService<SharedDhtState>() ?? new SharedDhtState(values, loggerFactory, options.RecordTtl) { KValue = options.KSize };
            builder.Services.AddSingleton<IValueStore>(values);
            builder.Services.AddSingleton<IProviderStore>(providers);
            builder.Services.AddSingleton<IRecordValidator>(validator);
            builder.Services.AddSingleton(state);
            builder.Services.AddKadDht();
            if (bootstrapNodes is not null)
                builder.Services.AddSingleton<DhtRuntime>(provider => DhtRuntime.Create(provider.GetRequiredService<ILocalPeer>(), provider, bootstrapNodes, state));
            var handler = new KadDhtProtocolExtensions.DhtHandlerServices(options, values, providers, validator,
                key => EnrichPeerAddresses(state.GetKNearestPeers(key), sp.GetService<PeerStore>()), node => state.AddNodeCallback?.Invoke(node));
            builder.AddProtocol(KadDhtProtocolExtensions.CreateProtocol(options, _ =>
            {
                // Handler-only builders have no local runtime to initialize. A configured runtime must initialize successfully.
                if (sp.GetService<ILocalPeer>() is { Identity: not null }) sp.GetService<KadDhtProtocol>();
                return handler;
            }, loggerFactory, timeProvider: sp.GetService<TimeProvider>()), options.Mode == KadDhtMode.Server,
                new InboundStreamLimit(options.MaxInboundStreams, options.MaxInboundStreamsPerPeer));
        }
    }

    /// <summary>
    /// Enriches a single DhtNode's addresses from PeerStore if available.
    /// PeerStore addresses come from Identify's cryptographically signed peer record
    /// and contain the peer's actual listen addresses (not ephemeral connection ports).
    /// </summary>
    internal static DhtNode EnrichNodeFromPeerStore(DhtNode node, PeerStore? peerStore)
    {
        if (peerStore == null) return node;

        try
        {
            if (peerStore.TryGetPeerInfo(node.PeerId, out PeerStore.PeerInfo? peerInfo) && peerInfo?.Addrs is { Count: > 0 })
            {
                return new DhtNode
                {
                    PeerId = node.PeerId,
                    PublicKey = node.PublicKey,
                    Multiaddrs = peerInfo.Addrs.Select(a => a.ToString()).ToArray()
                };
            }
        }
        catch { }

        return node;
    }

    /// <summary>
    /// Enriches an array of DhtNodes with addresses from PeerStore.
    /// </summary>
    internal static DhtNode[] EnrichPeerAddresses(DhtNode[] peers, PeerStore? peerStore)
    {
        if (peerStore == null || peers.Length == 0) return peers;

        var result = new DhtNode[peers.Length];
        for (int i = 0; i < peers.Length; i++)
        {
            result[i] = EnrichNodeFromPeerStore(peers[i], peerStore);
        }
        return result;
    }

    internal static void StorePeerAddresses(DhtNode node, PeerStore? peerStore)
    {
        if (peerStore == null) return;
        try
        {
            if (peerStore.TryGetPeerInfo(node.PeerId, out PeerStore.PeerInfo? existingInfo) && existingInfo?.SignedPeerRecord is not null && existingInfo.Addrs is { Count: > 0 })
                return;

            var addresses = new List<Multiaddress>(node.Multiaddrs.Count);
            foreach (string address in node.Multiaddrs)
            {
                if (string.IsNullOrWhiteSpace(address))
                    continue;

                if (NormalizePeerAddress(address, node.PeerId) is { } normalized)
                    addresses.Add(normalized);
            }

            if (addresses.Count > 0)
                peerStore.Discover(addresses.ToArray(), isTrusted: false);
        }
        catch { }
    }

    private static Multiaddress? NormalizePeerAddress(string address, PeerId peerId)
    {
        var multiaddress = Multiaddress.Decode(address);
        var addressPeerId = multiaddress.GetPeerId();

        if (addressPeerId is null)
            return multiaddress.Add<P2P>(peerId.ToString());

        return addressPeerId.Equals(peerId) ? multiaddress : null;
    }
}
