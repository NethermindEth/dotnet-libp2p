// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.RequestResponse;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

public static class ServiceCollectionExtensions
{
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

        services.AddSingleton<SharedDhtState>(sp => new SharedDhtState(null, sp.GetService<ILoggerFactory>()));

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

    public static ILibp2pPeerFactoryBuilder WithKadDht(this ILibp2pPeerFactoryBuilder builder)
    {
        var loggerFactory = builder.ServiceProvider.GetService<ILoggerFactory>();
        var logger = loggerFactory?.CreateLogger("KadDht.Handlers");
        var sharedState = builder.ServiceProvider.GetService<SharedDhtState>();
        var peerStore = builder.ServiceProvider.GetService<PeerStore>();
        var localPeer = builder.ServiceProvider.GetService<ILocalPeer>();

        // Set the local peer's public key in SharedDhtState for self-filtering
        if (sharedState != null && localPeer != null)
        {
            var localPublicKey = new PublicKey(localPeer.Identity.PeerId.Bytes.ToArray());
            sharedState.LocalPeerKey = localPublicKey;
        }

        logger?.LogInformation("Registering DHT protocol handlers");

        void AddRequestingPeer(ISessionContext context, string protocolName)
        {
            if (sharedState == null || peerStore == null) return;
            var remotePeerId = context.State.RemoteAddress?.GetPeerId();
            if (remotePeerId == null) return;

            try
            {
                var publicKey = new PublicKey(remotePeerId.Bytes);
                
                // Get advertised addresses from PeerStore (populated by Identify protocol)
                var peerInfo = peerStore.GetPeerInfo(remotePeerId);
                var multiaddrs = peerInfo?.Addrs?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>();
                
                // Fallback to connection address if no advertised addresses available yet
                if (multiaddrs.Length == 0)
                {
                    multiaddrs = new[] { context.State.RemoteAddress?.ToString() ?? "" };
                    logger?.LogDebug("{Protocol}: Using connection address for {PeerId} (Identify not complete)", protocolName, remotePeerId);
                }
                else
                {
                    logger?.LogInformation("{Protocol}: Using advertised addresses for {PeerId}: {Addrs}", protocolName, remotePeerId, string.Join(", ", multiaddrs));
                }
                
                var dhtNode = new DhtNode
                {
                    PeerId = remotePeerId,
                    PublicKey = publicKey,
                    Multiaddrs = multiaddrs
                };
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "{Protocol}: Failed to add peer", protocolName);
            }
        }

        builder.AddRequestResponseProtocol<PingRequest, PingResponse>("/ipfs/kad/1.0.0/ping",
            (request, context) =>
            {
                var remotePeer = context.State.RemoteAddress?.GetPeerId()?.ToString() ?? "unknown";
                logger?.LogInformation("PING: Handler invoked from {PeerId}", remotePeer);
                AddRequestingPeer(context, "PING");
                return Task.FromResult(new PingResponse());
            },
            isExposed: true);

        builder.AddRequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>("/ipfs/kad/1.0.0/find_node",
            (request, context) =>
            {
                var remotePeerId = context.State.RemoteAddress?.GetPeerId();
                var remotePeerIdStr = remotePeerId?.ToString() ?? "unknown";
                logger?.LogInformation("FIND_NODE: Handler invoked from {PeerId}", remotePeerIdStr);
                AddRequestingPeer(context, "FIND_NODE");

                var response = new FindNeighboursResponse();
                if (sharedState != null && request.Target?.Value != null)
                {
                    var targetBytes = request.Target.Value.ToByteArray();
                    var targetKey = new PublicKey(targetBytes);
                    
                    // Fetch more peers than needed to account for filtering
                    var nearestPeers = sharedState.GetKNearestPeers(targetKey, k: 20);

                    // Filter out the requesting peer AND the local peer
                    var localPeerKey = sharedState.LocalPeerKey;
                    var filteredPeers = nearestPeers
                        .Where(peer => remotePeerId == null || !peer.PeerId.Equals(remotePeerId)) // Filter requester
                        .Where(peer => localPeerKey == null || !peer.PublicKey.Equals(localPeerKey)) // Filter self
                        .Take(16);

                    foreach (var peer in filteredPeers)
                    {
                        var node = new Nethermind.Libp2P.Protocols.KadDht.Dto.Node
                        {
                            PublicKey = Google.Protobuf.ByteString.CopyFrom(peer.PublicKey.Bytes)
                        };
                        if (peer.Multiaddrs != null)
                        {
                            foreach (var addr in peer.Multiaddrs)
                            {
                                node.Multiaddrs.Add(addr);
                            }
                        }
                        response.Neighbours.Add(node);
                    }
                    logger?.LogInformation("FIND_NODE: Returning {Count} neighbors (filtered from {Total} candidates, excluding requester)", 
                        response.Neighbours.Count, nearestPeers.Count());
                }
                return Task.FromResult(response);
            },
            isExposed: true);

        builder.AddRequestResponseProtocol<PutValueRequest, PutValueResponse>("/ipfs/kad/1.0.0/put_value",
            (request, context) =>
            {
                var remotePeer = context.State.RemoteAddress?.GetPeerId()?.ToString() ?? "unknown";
                logger?.LogInformation("PUT_VALUE: Handler invoked from {PeerId}", remotePeer);
                AddRequestingPeer(context, "PUT_VALUE");

                var response = new PutValueResponse { Success = false, Error = "No value provided" };
                
                if (sharedState != null && request.Key != null && request.Value != null)
                {
                    try
                    {
                        var success = sharedState.ValueStore.Put(
                            key: request.Key.ToByteArray(),
                            value: request.Value.ToByteArray(),
                            signature: request.Signature?.ToByteArray(),
                            timestamp: request.Timestamp,
                            publisher: request.Publisher?.ToByteArray()
                        );

                        response = new PutValueResponse 
                        { 
                            Success = success, 
                            Error = success ? "" : "Value not stored (older timestamp)" 
                        };

                        logger?.LogInformation("PUT_VALUE: Stored value with success={Success} from {PeerId}", 
                            success, remotePeer);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "PUT_VALUE: Failed to store value from {PeerId}", remotePeer);
                        response = new PutValueResponse { Success = false, Error = ex.Message };
                    }
                }

                return Task.FromResult(response);
            },
            isExposed: true);

        builder.AddRequestResponseProtocol<GetValueRequest, GetValueResponse>("/ipfs/kad/1.0.0/get_value",
            (request, context) =>
            {
                var remotePeer = context.State.RemoteAddress?.GetPeerId()?.ToString() ?? "unknown";
                logger?.LogInformation("GET_VALUE: Handler invoked from {PeerId}", remotePeer);
                AddRequestingPeer(context, "GET_VALUE");

                var response = new GetValueResponse { Found = false };

                if (sharedState != null && request.Key != null)
                {
                    try
                    {
                        var dhtValue = sharedState.ValueStore.Get(request.Key.ToByteArray());
                        if (dhtValue != null)
                        {
                            response = new GetValueResponse
                            {
                                Found = true,
                                Value = Google.Protobuf.ByteString.CopyFrom(dhtValue.Value),
                                Signature = dhtValue.Signature != null 
                                    ? Google.Protobuf.ByteString.CopyFrom(dhtValue.Signature) 
                                    : Google.Protobuf.ByteString.Empty,
                                Timestamp = dhtValue.Timestamp,
                                Publisher = dhtValue.Publisher != null 
                                    ? Google.Protobuf.ByteString.CopyFrom(dhtValue.Publisher) 
                                    : Google.Protobuf.ByteString.Empty
                            };

                            logger?.LogInformation("GET_VALUE: Found value for key from {PeerId}", remotePeer);
                        }
                        else
                        {
                            logger?.LogDebug("GET_VALUE: Value not found for key from {PeerId}", remotePeer);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "GET_VALUE: Failed to retrieve value from {PeerId}", remotePeer);
                    }
                }

                return Task.FromResult(response);
            },
            isExposed: true);

        logger?.LogInformation("DHT protocol handlers registered");
        return builder;
    }
}
