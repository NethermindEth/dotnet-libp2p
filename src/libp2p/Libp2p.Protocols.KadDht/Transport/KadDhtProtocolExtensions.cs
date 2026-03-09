// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

public readonly record struct TestNode(PeerId Id);

public static class KadDhtProtocolExtensions
{
    public const string DefaultBaseId = "/ipfs/kad/1.0.0";

    /// <summary>
    /// Registers the unified /ipfs/kad/1.0.0 protocol handler with Message-based request/response.
    /// </summary>
    public static IPeerFactoryBuilder AddKadDhtProtocols(
        this IPeerFactoryBuilder builder,
        Func<PublicKey, IEnumerable<DhtNode>> findNearest,
        Action<DhtNode>? onPeerSeen = null,
        string baseId = DefaultBaseId,
        ILoggerFactory? loggerFactory = null,
        bool isExposed = true,
        KadDhtOptions? options = null,
        IValueStore? valueStore = null,
        IProviderStore? providerStore = null,
        IRecordValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(findNearest);

        var dhtOptions = options ?? new KadDhtOptions();
        var dhtValueStore = valueStore ?? new InMemoryValueStore(dhtOptions.MaxStoredValues, loggerFactory);
        var dhtProviderStore = providerStore ?? new InMemoryProviderStore(dhtOptions.MaxProvidersPerKey, loggerFactory);
        var dhtValidator = validator ?? DefaultRecordValidator.Instance;

        builder = builder.AddRequestResponseProtocol<Message, Message>(
            baseId,
            async (request, ctx) =>
            {
                TryTrackPeer(ctx, onPeerSeen);

                return request.Type switch
                {
                    Message.Types.MessageType.Ping => MessageHelper.CreatePingResponse(),

                    Message.Types.MessageType.FindNode => HandleFindNode(request, findNearest, ctx),

                    Message.Types.MessageType.PutValue => await HandlePutValue(request, dhtOptions, dhtValueStore, dhtValidator, loggerFactory),

                    Message.Types.MessageType.GetValue => await HandleGetValue(request, dhtValueStore, findNearest, loggerFactory),

                    Message.Types.MessageType.AddProvider => await HandleAddProvider(request, ctx, dhtOptions, dhtProviderStore, loggerFactory),

                    Message.Types.MessageType.GetProviders => await HandleGetProviders(request, dhtOptions, dhtProviderStore, findNearest, loggerFactory),

                    _ => new Message()
                };
            },
            isExposed: isExposed);

        return builder;
    }

    private static Message HandleFindNode(Message request, Func<PublicKey, IEnumerable<DhtNode>> findNearest, ISessionContext ctx)
    {
        if (!request.HasKey) return MessageHelper.CreateFindNodeResponse(Enumerable.Empty<DhtNode>());
        var target = new PublicKey(request.Key.ToByteArray());
        var remotePeerId = ctx.State.RemoteAddress?.GetPeerId();
        var neighbours = findNearest(target)
            .Where(n => remotePeerId == null || !n.PeerId.Equals(remotePeerId));
        return MessageHelper.CreateFindNodeResponse(neighbours);
    }

    private static async Task<Message> HandlePutValue(Message request, KadDhtOptions options,
        IValueStore valueStore, IRecordValidator validator, ILoggerFactory? loggerFactory)
    {
        try
        {
            if (options.Mode != KadDhtMode.Server)
                return MessageHelper.CreatePutValueResponse();

            if (request.Record == null || request.Record.Value.Length > options.MaxValueSize)
                return MessageHelper.CreatePutValueResponse();

            byte[] key = request.Key.ToByteArray();
            byte[] value = request.Record.Value.ToByteArray();

            if (!validator.Validate(key, value))
                return MessageHelper.CreatePutValueResponse();

            var storedValue = new StoredValue
            {
                Value = value,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = options.RecordTtl
            };

            await valueStore.PutValueAsync(key, storedValue);
            return MessageHelper.CreatePutValueResponse(request.Record);
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling PutValue: {Error}", ex.Message);
            return MessageHelper.CreatePutValueResponse();
        }
    }

    private static async Task<Message> HandleGetValue(Message request, IValueStore valueStore,
        Func<PublicKey, IEnumerable<DhtNode>> findNearest, ILoggerFactory? loggerFactory)
    {
        try
        {
            var target = new PublicKey(request.Key.ToByteArray());
            var closerPeers = findNearest(target);

            var storedValue = await valueStore.GetValueAsync(request.Key.ToByteArray());
            if (storedValue != null)
            {
                var record = new Record
                {
                    Key = request.Key,
                    Value = ByteString.CopyFrom(storedValue.Value),
                    TimeReceived = DateTimeOffset.FromUnixTimeSeconds(storedValue.Timestamp).ToString("o")
                };
                return MessageHelper.CreateGetValueResponse(record, closerPeers);
            }
            return MessageHelper.CreateGetValueResponse(closerPeers: closerPeers);
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling GetValue: {Error}", ex.Message);
            return MessageHelper.CreateGetValueResponse();
        }
    }

    private static async Task<Message> HandleAddProvider(Message request, ISessionContext ctx, KadDhtOptions options,
        IProviderStore providerStore, ILoggerFactory? loggerFactory)
    {
        try
        {
            if (options.Mode != KadDhtMode.Server)
                return new Message { Type = Message.Types.MessageType.AddProvider };

            var senderPeerId = ctx.State.RemoteAddress?.GetPeerId();

            foreach (var wirePeer in request.ProviderPeers)
            {
                var providerPeerId = new PeerId(wirePeer.Id.ToByteArray());

                if (senderPeerId != null && !providerPeerId.Equals(senderPeerId))
                {
                    loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                        ?.LogWarning("ADD_PROVIDER rejected: provider PeerId {ProviderId} does not match sender {SenderId}",
                            providerPeerId, senderPeerId);
                    continue;
                }

                var providerRecord = new ProviderRecord
                {
                    PeerId = providerPeerId,
                    Multiaddrs = wirePeer.Addrs.Select(a =>
                    {
                        try { return Multiformats.Address.Multiaddress.Decode(a.ToByteArray()).ToString(); }
                        catch { return ""; }
                    }).Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ttl = options.ProviderRecordTtl
                };

                await providerStore.AddProviderAsync(request.Key.ToByteArray(), providerRecord);
            }

            return new Message { Type = Message.Types.MessageType.AddProvider };
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling AddProvider: {Error}", ex.Message);
            return new Message { Type = Message.Types.MessageType.AddProvider };
        }
    }

    private static async Task<Message> HandleGetProviders(Message request, KadDhtOptions options, IProviderStore providerStore,
        Func<PublicKey, IEnumerable<DhtNode>> findNearest, ILoggerFactory? loggerFactory)
    {
        try
        {
            var providers = await providerStore.GetProvidersAsync(request.Key.ToByteArray(), options.MaxProvidersPerKey);
            var target = new PublicKey(request.Key.ToByteArray());
            var response = MessageHelper.CreateGetProvidersResponse(
                providerPeers: providers.Select(p => new Integration.DhtNode
                {
                    PeerId = p.PeerId,
                    PublicKey = new PublicKey(p.PeerId.Bytes),
                    Multiaddrs = p.Multiaddrs
                }),
                closerPeers: findNearest(target));
            return response;
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling GetProviders: {Error}", ex.Message);
            return MessageHelper.CreateGetProvidersResponse();
        }
    }

    private static void TryTrackPeer(ISessionContext ctx, Action<DhtNode>? onPeerSeen)
    {
        if (onPeerSeen == null) return;
        var remotePeerId = ctx.State.RemoteAddress?.GetPeerId();
        if (remotePeerId == null) return;
        try
        {
            onPeerSeen(new DhtNode
            {
                PeerId = remotePeerId,
                PublicKey = new PublicKey(remotePeerId.Bytes),
                Multiaddrs = new[] { ctx.State.RemoteAddress?.ToString() ?? "" }
            });
        }
        catch { }
    }
}
