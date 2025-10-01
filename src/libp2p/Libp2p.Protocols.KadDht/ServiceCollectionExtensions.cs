// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
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

        // Register message sender by use LibP2pKademliaMessageSender for real network operations
        services.AddSingleton<Kademlia.IKademliaMessageSender<PublicKey, DhtNode>>(sp => 
        {
            var localPeer = sp.GetRequiredService<ILocalPeer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return (Kademlia.IKademliaMessageSender<PublicKey, DhtNode>)new Network.LibP2pKademliaMessageSender<PublicKey, DhtNode>(localPeer, loggerFactory);
        });

        // Register main KadDht protocol dependency injection
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
        // Protocol handlers that will delegate to the KadDht service
        async Task<PingResponse> HandlePingRequest(PingRequest request, ISessionContext context)
        {
            // To do: Just return a successful ping response for now, next step shuld include routing table updates
            return new PingResponse();
        }

        async Task<FindNeighboursResponse> HandleFindNeighboursRequest(FindNeighboursRequest request, ISessionContext context)
        {
            // To do: query the local routing table
            return new FindNeighboursResponse();
        }

        async Task<PutValueResponse> HandlePutValueRequest(PutValueRequest request, ISessionContext context)
        {
            try
            {
                // To do: tdelegate to the KadDhtProtocol service
                return new PutValueResponse { Success = true };
            }
            catch
            {
                return new PutValueResponse { Success = false };
            }
        }

        async Task<GetValueResponse> HandleGetValueRequest(GetValueRequest request, ISessionContext context)
        {
            try
            {
                // To do: delegate to the KadDhtProtocol service
                return new GetValueResponse { Found = false };
            }
            catch
            {
                return new GetValueResponse { Found = false };
            }
        }

        async Task<AddProviderResponse> HandleAddProviderRequest(AddProviderRequest request, ISessionContext context)
        {
            try
            {
                // To do: replace with actual implementation
                return new AddProviderResponse { Success = true };
            }
            catch
            {
                return new AddProviderResponse { Success = false };
            }
        }

        async Task<GetProvidersResponse> HandleGetProvidersRequest(GetProvidersRequest request, ISessionContext context)
        {
            try
            {
                // To do: delegate to the KadDhtProtocol service
                return new GetProvidersResponse();
            }
            catch
            {
                return new GetProvidersResponse();
            }
        }

        builder.AddRequestResponseProtocol<PingRequest, PingResponse>("/ipfs/kad/1.0.0/ping", HandlePingRequest);
        builder.AddRequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>("/ipfs/kad/1.0.0/find_neighbours", HandleFindNeighboursRequest);
        builder.AddRequestResponseProtocol<PutValueRequest, PutValueResponse>("/ipfs/kad/1.0.0/put_value", HandlePutValueRequest);
        builder.AddRequestResponseProtocol<GetValueRequest, GetValueResponse>("/ipfs/kad/1.0.0/get_value", HandleGetValueRequest);
        builder.AddRequestResponseProtocol<AddProviderRequest, AddProviderResponse>("/ipfs/kad/1.0.0/add_provider", HandleAddProviderRequest);
        builder.AddRequestResponseProtocol<GetProvidersRequest, GetProvidersResponse>("/ipfs/kad/1.0.0/get_providers", HandleGetProvidersRequest);

        return builder;
    }
}
