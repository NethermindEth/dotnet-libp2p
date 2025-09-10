using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Libp2p.Protocols.KadDht.Storage;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Service collection extensions for Kademlia DHT protocol registration
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Kademlia DHT stream protocol to the libp2p host
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddKademliaDhtStreamProtocol(this IServiceCollection services)
    {
        // Register core DHT services if not already registered
        services.TryAddSingleton<IValueStore, InMemoryValueStore>();
        services.TryAddSingleton<IProviderStore, InMemoryProviderStore>();
        
        // Register message sender for demo compatibility
        services.TryAddScoped<LibP2pKademliaMessageSender>();

        return services;
    }

    /// <summary>
    /// Adds Kademlia DHT to the service collection using KadDht alias
    /// </summary>
    public static IServiceCollection AddKadDht(this IServiceCollection services, Action<KadDhtOptions>? configure = null)
    {
        services.AddKademliaDhtStreamProtocol();
        
        if (configure != null)
        {
            services.Configure(configure);
        }
        
        return services;
    }
}

/// <summary>
/// LibP2P peer factory builder extensions for Kademlia DHT
/// </summary>
public static class LibP2pPeerFactoryBuilderExtensions
{
    /// <summary>
    /// Adds Kademlia DHT protocol to the peer factory builder
    /// </summary>
    public static ILibp2pPeerFactoryBuilder WithKadDht(this ILibp2pPeerFactoryBuilder builder)
    {
        // For now, this is a placeholder method that enables KadDht support
        // The actual protocol registration happens through the service collection
        return builder;
    }
}
