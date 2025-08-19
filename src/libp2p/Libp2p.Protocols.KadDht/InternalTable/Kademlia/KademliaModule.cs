// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Microsoft.Extensions.DependencyInjection;


using Nethermind.Network.Discovery.Discv4;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Helper class for registering Kademlia services with the dependency injection container.
/// </summary>
public static class KademliaModule
{
    /// <summary>
    /// Registers Kademlia services with the service collection.
    /// </summary>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TNode">The node type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection RegisterKademlia<TKey, TNode>(this IServiceCollection services)
    {
        services.AddSingleton<KademliaConfig<TNode>>();
        services.AddSingleton<INodeHashProvider<TNode>, FromKeyNodeHashProvider<TKey, TNode>>();
        services.AddSingleton<IRoutingTable<TNode>, KBucketTree<TNode>>();
        services.AddSingleton<ILookupAlgo<TNode>, LookupKNearestNeighbour<TKey, TNode>>();
        services.AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TKey, TNode>>();
        services.AddSingleton<IIteratorNodeLookup<TKey, TNode>, IteratorNodeLookup<TKey, TNode>>();
        services.AddSingleton<IKademlia<TKey, TNode>, Kademlia<TKey, TNode>>();
        
        return services;
    }
}

