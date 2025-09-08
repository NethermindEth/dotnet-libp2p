// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht;
using System.Reflection;

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

        // Configure options
        var options = new KadDhtOptions();
        configure?.Invoke(options);

        // Obtain the IServiceCollection via a reflected 'Services' property on the concrete builder implementation.
        var servicesProperty = builder.GetType().GetProperty("Services", BindingFlags.Public | BindingFlags.Instance);
        if (servicesProperty?.GetValue(builder) is not IServiceCollection services)
        {
            throw new InvalidOperationException("Unable to access underlying Services collection from IPeerFactoryBuilder (expected a public 'Services' property).");
        }

    services.AddSingleton<KadDhtProtocol>(serviceProvider =>
        {
            var localPeer = serviceProvider.GetRequiredService<ILocalPeer>();
            var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

            return new KadDhtProtocol(
                localPeer,
                loggerFactory,
                options,
                valueStore,
                providerStore,
                bootstrapNodes);
        });
    // Register the high level session protocol once
    builder = builder.AddProtocol<KadDhtProtocol>();

        // Register the protocol extensions for network handlers
        var dhtOptions = options;
        var dhtValueStore = valueStore ?? new InMemoryValueStore(dhtOptions.MaxStoredValues);
        var dhtProviderStore = providerStore ?? new InMemoryProviderStore(dhtOptions.MaxProvidersPerKey);

        // Add the request-response protocol handlers
        return builder.AddKadDhtProtocols(
            // Temporary mapping to TestNode for legacy request/response layer (returns empty set for now)
            publicKey => Array.Empty<TestNode>(),
            options: dhtOptions,
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
        => new DhtNode(peerId, new Kademlia.PublicKey(peerId.Bytes.ToArray()), multiaddrs);

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
