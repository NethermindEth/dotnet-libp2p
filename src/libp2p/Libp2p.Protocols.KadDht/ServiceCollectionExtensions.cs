// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Libp2p.Protocols.KadDht;

public static class ServiceCollectionExtensions
{
    public const string ProtocolId = "/ipfs/kad/1.0.0";

    public static IServiceCollection AddKadDht(this IServiceCollection services,
        Action<KadDhtOptions>? configureOptions = null)
    {
        var options = new KadDhtOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        services.AddSingleton<IValueStore>(sp =>
            new InMemoryValueStore(options.MaxStoredValues, sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IProviderStore>(sp =>
            new InMemoryProviderStore(options.MaxProvidersPerKey, sp.GetService<ILoggerFactory>()));

        services.AddSingleton<SharedDhtState>(sp =>
        {
            var state = new SharedDhtState(null, sp.GetService<ILoggerFactory>());
            state.KValue = options.KSize;
            return state;
        });

        services.AddSingleton<Integration.LibP2pKademliaMessageSender>(sp =>
        {
            var localPeer = sp.GetRequiredService<ILocalPeer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var peerStore = sp.GetService<PeerStore>();
            return new Integration.LibP2pKademliaMessageSender(localPeer, loggerFactory,
                onPeerDiscovered: peerStore is not null ? node => StorePeerAddresses(node, peerStore) : null);
        });
        services.AddSingleton<Integration.IDhtMessageSender>(sp => sp.GetRequiredService<Integration.LibP2pKademliaMessageSender>());
        services.AddSingleton<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>(sp =>
            sp.GetRequiredService<Integration.LibP2pKademliaMessageSender>());

        services.AddSingleton<KadDhtProtocol>(sp =>
        {
            var localPeer = sp.GetRequiredService<ILocalPeer>();
            var messageSender = sp.GetRequiredService<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>();
            var dhtMessageSender = sp.GetRequiredService<Integration.IDhtMessageSender>();
            var kadDhtOptions = sp.GetRequiredService<KadDhtOptions>();
            var valueStore = sp.GetRequiredService<IValueStore>();
            var providerStore = sp.GetRequiredService<IProviderStore>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            var protocol = new KadDhtProtocol(localPeer, messageSender, dhtMessageSender, kadDhtOptions, valueStore, providerStore, loggerFactory);

            var sharedState = sp.GetService<SharedDhtState>();
            if (protocol.RoutingTable != null && sharedState != null)
                sharedState.SetRoutingTable(protocol.RoutingTable);
            if (sharedState != null)
                sharedState.AddNodeCallback = protocol.AddNode;

            return protocol;
        });

        return services;
    }

    public static ILibp2pPeerFactoryBuilder WithKadDht(this ILibp2pPeerFactoryBuilder builder)
    {
        var serviceProvider = builder.ServiceProvider;
        var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
        var options = serviceProvider.GetService<KadDhtOptions>() ?? new KadDhtOptions();
        var sharedState = serviceProvider.GetService<SharedDhtState>();
        if (sharedState != null)
            sharedState.KValue = options.KSize;
        var valueStore = serviceProvider.GetService<IValueStore>()
            ?? new InMemoryValueStore(options.MaxStoredValues, loggerFactory);
        var providerStore = serviceProvider.GetService<IProviderStore>()
            ?? new InMemoryProviderStore(options.MaxProvidersPerKey, loggerFactory);
        var peerStore = serviceProvider.GetService<PeerStore>();

        bool initialized = false;
        KadDhtProtocol? kadDhtProtocol = null;

        void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;

            try
            {
                kadDhtProtocol = serviceProvider.GetService<KadDhtProtocol>();
            }
            catch (InvalidOperationException) { }

            if (sharedState != null)
            {
                if (sharedState.LocalPeerKey == null)
                {
                    try
                    {
                        var localPeer = serviceProvider.GetService<ILocalPeer>();
                        if (localPeer != null)
                            sharedState.LocalPeerKey = new PublicKey(localPeer.Identity.PeerId.Bytes.ToArray());
                    }
                    catch (InvalidOperationException) { }
                }

                if (kadDhtProtocol?.RoutingTable != null)
                    sharedState.SetRoutingTable(kadDhtProtocol.RoutingTable);

                if (kadDhtProtocol != null)
                    sharedState.AddNodeCallback = kadDhtProtocol.AddNode;
            }
        }

        // Delegate to AddKadDhtProtocols so all incoming message handling uses the same code path
        builder.AddKadDhtProtocols(
            findNearest: publicKey =>
            {
                EnsureInitialized();
                var peers = sharedState?.GetKNearestPeers(publicKey) ?? Array.Empty<DhtNode>();
                // Enrich addresses from PeerStore (which has correct listen addresses from Identify exchange)
                // before sending in FindNode responses
                return EnrichPeerAddresses(peers, peerStore);
            },
            onPeerSeen: node =>
            {
                EnsureInitialized();
                // Enrich with addresses from PeerStore (populated by Identify's signed peer record)
                // to avoid storing ephemeral source-port addresses in the routing table
                var enrichedNode = EnrichNodeFromPeerStore(node, peerStore);
                sharedState?.AddNodeCallback?.Invoke(enrichedNode);
                StorePeerAddresses(node, peerStore);
            },
            loggerFactory: loggerFactory,
            isExposed: options.Mode == KadDhtMode.Server,
            options: options,
            valueStore: valueStore,
            providerStore: providerStore);

        return builder;
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
            var peerInfo = peerStore.GetPeerInfo(node.PeerId);
            if (peerInfo.Addrs is { Count: > 0 })
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
            var existingInfo = peerStore.GetPeerInfo(node.PeerId);
            if (existingInfo.SignedPeerRecord is not null && existingInfo.Addrs is { Count: > 0 })
                return;

            peerStore.Discover(node.Multiaddrs
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => Multiformats.Address.Multiaddress.Decode(a))
                .ToArray());
        }
        catch { }
    }
}
