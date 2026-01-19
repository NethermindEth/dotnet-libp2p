// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.RequestResponse;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Service collection extensions for registering KadDht with libp2p.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add KadDht protocol and all related services to the service collection.
    /// </summary>
    public static IServiceCollection AddKadDht(this IServiceCollection services,
        Action<KadDhtOptions>? configureOptions = null)
    {
        var options = new KadDhtOptions();
        configureOptions?.Invoke(options);
        services.AddSingleton(options);

        // Register storage services
        services.AddSingleton<IValueStore>(sp =>
            new InMemoryValueStore(options.MaxStoredValues, sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IProviderStore>(sp =>
            new InMemoryProviderStore(options.MaxProvidersPerKey, sp.GetService<ILoggerFactory>()));

        // Register shared routing table state for protocol handlers
        // This allows incoming DHT requests to query and update the routing table
        services.AddSingleton<SharedDhtState>(sp => new SharedDhtState(sp.GetService<ILoggerFactory>()));

        // Register message sender by use LibP2pKademliaMessageSender for real network operations
        services.AddSingleton<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>(sp =>
        {
            var localPeer = sp.GetRequiredService<ILocalPeer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new Integration.LibP2pKademliaMessageSender(localPeer, loggerFactory);
        });

        services.AddSingleton<KadDhtProtocol>(sp =>
        {
            var localPeer = sp.GetRequiredService<ILocalPeer>();
            var messageSender = sp.GetRequiredService<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>();
            var kadDhtOptions = sp.GetRequiredService<KadDhtOptions>();
            var valueStore = sp.GetRequiredService<IValueStore>();
            var providerStore = sp.GetRequiredService<IProviderStore>();
            var loggerFactory = sp.GetService<ILoggerFactory>();

            return new KadDhtProtocol(localPeer, messageSender, kadDhtOptions, valueStore, providerStore, loggerFactory);
        });

        return services;
    }

    /// <summary>
    /// Configure the libp2p peer factory builder to include KadDht protocols.
    /// </summary>
    public static ILibp2pPeerFactoryBuilder WithKadDht(this ILibp2pPeerFactoryBuilder builder)
    {
        var loggerFactory = builder.ServiceProvider.GetService<ILoggerFactory>();

        builder.AddProtocol(new KadDhtPingProtocol(loggerFactory), isExposed: true);

        var sharedState = builder.ServiceProvider.GetService<SharedDhtState>();

        if (sharedState != null)
        {
        }

        builder.AddRequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>("/ipfs/kad/1.0.0/find_node",
            (request, context) =>
            {
                // Enhanced find neighbours with target key logging and actual routing table queries
                try
                {
                    var remotePeer = context.State.RemoteAddress?.ToString() ?? "unknown";
                    var remotePeerId = context.State.RemoteAddress?.GetPeerId();
                    var targetKeyHex = request.Target?.Value?.ToByteArray() != null
                        ? Convert.ToHexString(request.Target.Value.ToByteArray()[..Math.Min(8, request.Target.Value.Length)])
                        : "null";

                    Console.WriteLine($"[DHT-FIND_NODE] Received find_node request from {remotePeer} for target {targetKeyHex}");

                    // Add requesting peer to routing table
                    if (sharedState != null && remotePeerId != null)
                    {
                        try
                        {
                            var publicKey = new PublicKey(remotePeerId.Bytes);
                            var dhtNode = new DhtNode
                            {
                                PeerId = remotePeerId,
                                PublicKey = publicKey,
                                Multiaddrs = new[] { remotePeer }
                            };
                            sharedState.AddOrUpdatePeer(publicKey, dhtNode);
                        }
                        catch { /* Ignore errors adding peer */ }
                    }

                    // Query routing table and return actual closest nodes
                    var response = new FindNeighboursResponse();

                    if (sharedState != null && request.Target?.Value != null)
                    {
                        try
                        {
                            // Convert target to PublicKey
                            var targetBytes = request.Target.Value.ToByteArray();
                            var targetKey = new PublicKey(targetBytes);

                            // Get K nearest neighbors from routing table
                            var nearestPeers = sharedState.GetKNearestPeers(targetKey, k: 16);

                            // Convert to response format
                            foreach (var peer in nearestPeers)
                            {
                                var node = new Nethermind.Libp2P.Protocols.KadDht.Dto.Node
                                {
                                    PublicKey = Google.Protobuf.ByteString.CopyFrom(peer.PublicKey.Bytes)
                                };

                                // Add multiaddresses if available
                                if (peer.Multiaddrs != null)
                                {
                                    foreach (var addr in peer.Multiaddrs)
                                    {
                                        node.Multiaddrs.Add(addr);
                                    }
                                }

                                response.Neighbours.Add(node);
                            }

                            Console.WriteLine($"[DHT-FIND_NODE] Returning {response.Neighbours.Count} neighbours from routing table (total: {sharedState.PeerCount} peers)");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DHT-FIND_NODE] Error querying routing table: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[DHT-FIND_NODE] No shared state or invalid target, returning empty response");
                    }

                    return Task.FromResult(response);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DHT-FIND_NODE] Error processing find_node: {ex.Message}");
                    return Task.FromResult(new FindNeighboursResponse());
                }
            });

        // Keep existing placeholder handlers for other protocols until they're implemented
        builder.AddRequestResponseProtocol<PutValueRequest, PutValueResponse>("/ipfs/kad/1.0.0/put_value",
            (request, context) => Task.FromResult(new PutValueResponse { Success = true }));
        builder.AddRequestResponseProtocol<GetValueRequest, GetValueResponse>("/ipfs/kad/1.0.0/get_value",
            (request, context) => Task.FromResult(new GetValueResponse { Found = false }));
        builder.AddRequestResponseProtocol<AddProviderRequest, AddProviderResponse>("/ipfs/kad/1.0.0/add_provider",
            (request, context) => Task.FromResult(new AddProviderResponse { Success = true }));
        builder.AddRequestResponseProtocol<GetProvidersRequest, GetProvidersResponse>("/ipfs/kad/1.0.0/get_providers",
            (request, context) => Task.FromResult(new GetProvidersResponse()));

        return builder;
    }
}
