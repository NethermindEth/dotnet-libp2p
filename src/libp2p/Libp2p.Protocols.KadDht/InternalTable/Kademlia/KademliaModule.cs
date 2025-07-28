// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Microsoft.Extensions.DependencyInjection;


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

/// <summary>
/// Helper class for registering Kademlia services with the dependency injection container.
/// </summary>
public static class KademliaModule
{
    /// <summary>
    /// Registers Kademlia services with the service collection.
    /// </summary>
    /// <typeparam name="TNode">The node type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection RegisterKademlia<TNode>(this IServiceCollection services)
    {
        services.AddSingleton<KademliaConfig<TNode>>();
        services.AddSingleton<INodeHashProvider<TNode>, FromKeyNodeHashProvider<TNode>>();
        services.AddSingleton<IRoutingTable<TNode, ValueHash256>, KBucketTree<TNode>>();
        services.AddSingleton<ILookupAlgo<TNode, ValueHash256>, LookupKNearestNeighbour<TNode>>();
        services.AddSingleton<INodeHealthTracker<TNode>, NodeHealthTracker<TNode>>();
        services.AddSingleton<IIteratorNodeLookup<TNode, ValueHash256>, IteratorNodeLookup<TNode, ValueHash256>>();
        services.AddSingleton<IKademlia<TNode, ValueHash256>, Kademlia<TNode, ValueHash256>>();

        return services;
    }
}
