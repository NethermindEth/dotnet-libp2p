// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
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
        var logger = loggerFactory?.CreateLogger("KadDht.Handler");
        var sharedState = builder.ServiceProvider.GetService<SharedDhtState>();
        var peerStore = builder.ServiceProvider.GetService<PeerStore>();
        var localPeer = builder.ServiceProvider.GetService<ILocalPeer>();

        if (sharedState != null && localPeer != null)
        {
            sharedState.LocalPeerKey = new PublicKey(localPeer.Identity.PeerId.Bytes.ToArray());
        }

        builder.AddRequestResponseProtocol<Message, Message>(ProtocolId,
            (request, context) => HandleMessage(request, context, sharedState, peerStore, localPeer, logger),
            isExposed: true);

        return builder;
    }

    private static Task<Message> HandleMessage(
        Message request,
        ISessionContext context,
        SharedDhtState? sharedState,
        PeerStore? peerStore,
        ILocalPeer? localPeer,
        ILogger? logger)
    {
        var remotePeerId = context.State.RemoteAddress?.GetPeerId();

        TryAddRequestingPeer(context, sharedState, peerStore, logger);

        return request.Type switch
        {
            Message.Types.MessageType.Ping => HandlePing(remotePeerId, logger),
            Message.Types.MessageType.FindNode => HandleFindNode(request, remotePeerId, sharedState, logger),
            Message.Types.MessageType.PutValue => HandlePutValue(request, remotePeerId, sharedState, logger),
            Message.Types.MessageType.GetValue => HandleGetValue(request, remotePeerId, sharedState, logger),
            Message.Types.MessageType.AddProvider => HandleAddProvider(request, remotePeerId, logger),
            Message.Types.MessageType.GetProviders => HandleGetProviders(request, remotePeerId, logger),
            _ => Task.FromResult(new Message())
        };
    }

    private static Task<Message> HandlePing(PeerId? remotePeerId, ILogger? logger)
    {
        logger?.LogInformation("PING from {PeerId}", remotePeerId);
        return Task.FromResult(MessageHelper.CreatePingResponse());
    }

    private static Task<Message> HandleFindNode(Message request, PeerId? remotePeerId, SharedDhtState? sharedState, ILogger? logger)
    {
        logger?.LogInformation("FIND_NODE from {PeerId}", remotePeerId);

        if (sharedState == null || !request.HasKey)
            return Task.FromResult(MessageHelper.CreateFindNodeResponse(Enumerable.Empty<DhtNode>()));

        var targetKey = new PublicKey(request.Key.ToByteArray());
        var nearestPeers = sharedState.GetKNearestPeers(targetKey, k: 20);

        var localPeerKey = sharedState.LocalPeerKey;
        var filteredPeers = nearestPeers
            .Where(peer => remotePeerId == null || !peer.PeerId.Equals(remotePeerId))
            .Where(peer => localPeerKey == null || !peer.PublicKey.Equals(localPeerKey))
            .Take(20);

        var response = MessageHelper.CreateFindNodeResponse(filteredPeers);
        logger?.LogInformation("FIND_NODE: Returning {Count} closer peers", response.CloserPeers.Count);
        return Task.FromResult(response);
    }

    private static Task<Message> HandlePutValue(Message request, PeerId? remotePeerId, SharedDhtState? sharedState, ILogger? logger)
    {
        logger?.LogInformation("PUT_VALUE from {PeerId}", remotePeerId);

        if (sharedState == null || !request.HasKey || request.Record == null)
            return Task.FromResult(MessageHelper.CreatePutValueResponse());

        try
        {
            var key = request.Key.ToByteArray();
            var value = request.Record.Value.ToByteArray();

            sharedState.ValueStore.Put(key, value, null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null);

            logger?.LogInformation("PUT_VALUE: Stored value from {PeerId}", remotePeerId);
            return Task.FromResult(MessageHelper.CreatePutValueResponse(request.Record));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "PUT_VALUE: Failed from {PeerId}", remotePeerId);
            return Task.FromResult(MessageHelper.CreatePutValueResponse());
        }
    }

    private static Task<Message> HandleGetValue(Message request, PeerId? remotePeerId, SharedDhtState? sharedState, ILogger? logger)
    {
        logger?.LogInformation("GET_VALUE from {PeerId}", remotePeerId);

        if (sharedState == null || !request.HasKey)
            return Task.FromResult(MessageHelper.CreateGetValueResponse());

        try
        {
            var key = request.Key.ToByteArray();
            var dhtValue = sharedState.ValueStore.Get(key);

            if (dhtValue != null)
            {
                var record = new Record
                {
                    Key = request.Key,
                    Value = ByteString.CopyFrom(dhtValue.Value),
                    TimeReceived = DateTimeOffset.FromUnixTimeSeconds(dhtValue.Timestamp).ToString("o")
                };

                logger?.LogInformation("GET_VALUE: Found value from {PeerId}", remotePeerId);
                return Task.FromResult(MessageHelper.CreateGetValueResponse(record));
            }

            var targetKey = new PublicKey(key);
            var closerPeers = sharedState.GetKNearestPeers(targetKey, k: 20)
                .Where(peer => remotePeerId == null || !peer.PeerId.Equals(remotePeerId));

            logger?.LogDebug("GET_VALUE: Not found, returning {Count} closer peers", closerPeers.Count());
            return Task.FromResult(MessageHelper.CreateGetValueResponse(closerPeers: closerPeers));
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "GET_VALUE: Failed from {PeerId}", remotePeerId);
            return Task.FromResult(MessageHelper.CreateGetValueResponse());
        }
    }

    private static Task<Message> HandleAddProvider(Message request, PeerId? remotePeerId, ILogger? logger)
    {
        logger?.LogInformation("ADD_PROVIDER from {PeerId}", remotePeerId);
        return Task.FromResult(new Message { Type = Message.Types.MessageType.AddProvider });
    }

    private static Task<Message> HandleGetProviders(Message request, PeerId? remotePeerId, ILogger? logger)
    {
        logger?.LogInformation("GET_PROVIDERS from {PeerId}", remotePeerId);
        return Task.FromResult(MessageHelper.CreateGetProvidersResponse());
    }

    private static void TryAddRequestingPeer(ISessionContext context, SharedDhtState? sharedState, PeerStore? peerStore, ILogger? logger)
    {
        if (sharedState == null) return;
        var remotePeerId = context.State.RemoteAddress?.GetPeerId();
        if (remotePeerId == null) return;

        try
        {
            var publicKey = new PublicKey(remotePeerId.Bytes);
            var peerInfo = peerStore?.GetPeerInfo(remotePeerId);
            var multiaddrs = peerInfo?.Addrs?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>();

            if (multiaddrs.Length == 0)
            {
                multiaddrs = new[] { context.State.RemoteAddress?.ToString() ?? "" };
            }

            _ = new DhtNode
            {
                PeerId = remotePeerId,
                PublicKey = publicKey,
                Multiaddrs = multiaddrs
            };
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to track requesting peer");
        }
    }
}
