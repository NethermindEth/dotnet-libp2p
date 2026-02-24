// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Extension methods for integrating the complete Kad-DHT implementation with libp2p.
/// </summary>
public static class KadDhtIntegrationExtensions
{
    /// <summary>
    /// Add complete Kad-DHT protocol with Kademlia algorithm integration to the peer factory.
    /// </summary>
    /// <param name="builder">The peer factory builder.</param>
    /// <param name="configure">Optional configuration action for DHT options.</param>
    /// <param name="bootstrapNodes">Optional bootstrap nodes for initial network discovery.</param>
    /// <param name="valueStore">Optional custom value store implementation.</param>
    /// <param name="providerStore">Optional custom provider store implementation.</param>
    /// <returns>The peer factory builder for chaining.</returns>
    public static IPeerFactoryBuilder AddKadDht(
        this IPeerFactoryBuilder builder,
        Action<KadDhtOptions>? configure = null,
        IEnumerable<DhtNode>? bootstrapNodes = null,
        IValueStore? valueStore = null,
        IProviderStore? providerStore = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Cast to ILibp2pPeerFactoryBuilder to access Services property
        if (builder is not ILibp2pPeerFactoryBuilder libp2pBuilder)
        {
            throw new InvalidOperationException(
                "AddKadDht requires an ILibp2pPeerFactoryBuilder implementation. " +
                "Use AddLibp2p() to create the builder.");
        }

        // Configure options
        var options = new KadDhtOptions();
        configure?.Invoke(options);

        // Access the service collection directly (no reflection needed!)
        var services = libp2pBuilder.Services;

        var dhtValueStore = valueStore ?? new InMemoryValueStore(options.MaxStoredValues);
        var dhtProviderStore = providerStore ?? new InMemoryProviderStore(options.MaxProvidersPerKey);

        services.AddSingleton<LibP2pKademliaMessageSender>(sp =>
        {
            var localPeer = sp.GetRequiredService<ILocalPeer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var peerStore = sp.GetService<Nethermind.Libp2p.Core.Discovery.PeerStore>();
            return new LibP2pKademliaMessageSender(localPeer, loggerFactory,
                onPeerDiscovered: peerStore is not null ? node => ServiceCollectionExtensions.StorePeerAddresses(node, peerStore) : null);
        });
        services.AddSingleton<IDhtMessageSender>(sp => sp.GetRequiredService<LibP2pKademliaMessageSender>());
        services.AddSingleton<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>(sp =>
            sp.GetRequiredService<LibP2pKademliaMessageSender>());

        // Shared state for routing table access from incoming request handlers
        var sharedState = new SharedDhtState();
        sharedState.KValue = options.KSize;
        services.AddSingleton(sharedState);

        services.AddSingleton<KadDhtProtocol>(serviceProvider =>
            {
                var localPeer = serviceProvider.GetRequiredService<ILocalPeer>();
                var loggerFactory = serviceProvider.GetService<ILoggerFactory>();
                var messageSender = serviceProvider.GetRequiredService<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>();
                var dhtMessageSender = serviceProvider.GetRequiredService<IDhtMessageSender>();

                var protocol = new KadDhtProtocol(
                    localPeer,
                    messageSender,
                    dhtMessageSender,
                    options,
                    dhtValueStore,
                    dhtProviderStore,
                    loggerFactory);

                if (protocol.RoutingTable != null)
                    sharedState.SetRoutingTable(protocol.RoutingTable);

                // Wire the AddNode callback now that the protocol is fully initialized.
                // Incoming connections that arrived before this point will have been
                // buffered in SharedDhtState; from now on they go straight to AddNode.
                sharedState.AddNodeCallback = protocol.AddNode;

                return protocol;
            });

        // Register the high level session protocol once
        builder = builder.AddProtocol<KadDhtProtocol>();

        // Add the request-response protocol handlers
        return builder.AddKadDhtProtocols(
            findNearest: publicKey => sharedState.GetKNearestPeers(publicKey),
            onPeerSeen: node =>
            {
                // Delegate to SharedDhtState.AddNodeCallback which is set once
                // KadDhtProtocol is resolved from DI. Before that, AddNodeCallback
                // is null and the peer is safely skipped (it will be re-discovered
                // during bootstrap or future lookups).
                sharedState.AddNodeCallback?.Invoke(node);
            },
            isExposed: options.Mode == KadDhtMode.Server,
            options: options,
            valueStore: dhtValueStore,
            providerStore: dhtProviderStore);
    }

    /// <summary>
    /// Create a DhtNode from libp2p PeerId and optional addresses.
    /// </summary>
    /// <param name="peerId">The peer ID.</param>
    /// <param name="multiaddrs">Optional multiaddresses for the node.</param>
    /// <returns>A DhtNode instance.</returns>
    // Conversion retained for potential future usage with full integration
    public static DhtNode ToDhtNode(this PeerId peerId, IEnumerable<string>? multiaddrs = null)
        => new DhtNode
        {
            PeerId = peerId,
            PublicKey = new Kademlia.PublicKey(peerId.Bytes.ToArray()),
            Multiaddrs = multiaddrs?.ToArray() ?? Array.Empty<string>()
        };

    /// <summary>
    /// Bootstrap and run the DHT in the background.
    /// Call this method once after peer initialization to start DHT participation.
    /// </summary>
    /// <param name="peer">The local peer instance.</param>
    /// <param name="cancellationToken">Cancellation token to stop the DHT.</param>
    /// <returns>Task that completes when the DHT is stopped.</returns>
    public static async Task RunKadDhtAsync(this ILocalPeer peer, CancellationToken cancellationToken = default)
    {
        // Get the DHT protocol from the peer
        var dhtProtocol = peer.GetProtocol<KadDhtProtocol>();
        if (dhtProtocol == null)
        {
            throw new InvalidOperationException("KadDhtProtocol not found. Make sure to call AddKadDht() when configuring the peer.");
        }

        // Bootstrap the DHT
        await dhtProtocol.BootstrapAsync(cancellationToken);

        // Run the background maintenance processes
        await dhtProtocol.RunAsync(cancellationToken);
    }

    /// <summary>
    /// Get the Kad-DHT protocol instance from a peer.
    /// </summary>
    /// <param name="peer">The local peer instance.</param>
    /// <returns>The KadDhtProtocol instance, or null if not configured.</returns>
    public static KadDhtProtocol? GetKadDht(this ILocalPeer peer)
    {
        return peer.GetProtocol<KadDhtProtocol>();
    }
}
