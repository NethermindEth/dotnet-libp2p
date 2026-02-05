// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Google.Protobuf;
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
        Func<PublicKey, IEnumerable<TestNode>> findNearest,
        string baseId = DefaultBaseId,
        ILoggerFactory? loggerFactory = null,
        bool isExposed = true,
        KadDhtOptions? options = null,
        IValueStore? valueStore = null,
        IProviderStore? providerStore = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(findNearest);

        var dhtOptions = options ?? new KadDhtOptions();
        var dhtValueStore = valueStore ?? new InMemoryValueStore(dhtOptions.MaxStoredValues, loggerFactory);
        var dhtProviderStore = providerStore ?? new InMemoryProviderStore(dhtOptions.MaxProvidersPerKey, loggerFactory);

        builder = builder.AddRequestResponseProtocol<Message, Message>(
            baseId,
            async (request, ctx) =>
            {
                return request.Type switch
                {
                    Message.Types.MessageType.Ping => MessageHelper.CreatePingResponse(),

                    Message.Types.MessageType.FindNode => HandleFindNode(request, findNearest),

                    Message.Types.MessageType.PutValue => await HandlePutValue(request, dhtOptions, dhtValueStore, loggerFactory),

                    Message.Types.MessageType.GetValue => await HandleGetValue(request, dhtValueStore, loggerFactory),

                    Message.Types.MessageType.AddProvider => await HandleAddProvider(request, dhtOptions, dhtProviderStore, loggerFactory),

                    Message.Types.MessageType.GetProviders => await HandleGetProviders(request, dhtProviderStore, loggerFactory),

                    _ => new Message()
                };
            },
            isExposed: isExposed);

        return builder;
    }

    private static Message HandleFindNode(Message request, Func<PublicKey, IEnumerable<TestNode>> findNearest)
    {
        var target = new PublicKey(request.Key.ToByteArray());
        var neighbours = findNearest(target).ToArray();
        var response = new Message { Type = Message.Types.MessageType.FindNode };
        foreach (var n in neighbours)
        {
            response.CloserPeers.Add(new Message.Types.Peer
            {
                Id = ByteString.CopyFrom(n.Id.Bytes.ToArray())
            });
        }
        return response;
    }

    private static async Task<Message> HandlePutValue(Message request, KadDhtOptions options,
        IValueStore valueStore, ILoggerFactory? loggerFactory)
    {
        try
        {
            if (options.Mode != KadDhtMode.Server)
                return MessageHelper.CreatePutValueResponse();

            if (request.Record == null || request.Record.Value.Length > options.MaxValueSize)
                return MessageHelper.CreatePutValueResponse();

            var storedValue = new StoredValue
            {
                Value = request.Record.Value.ToByteArray(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Ttl = options.RecordTtl
            };

            await valueStore.PutValueAsync(request.Key.ToByteArray(), storedValue);
            return MessageHelper.CreatePutValueResponse(request.Record);
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling PutValue: {Error}", ex.Message);
            return MessageHelper.CreatePutValueResponse();
        }
    }

    private static async Task<Message> HandleGetValue(Message request, IValueStore valueStore, ILoggerFactory? loggerFactory)
    {
        try
        {
            var storedValue = await valueStore.GetValueAsync(request.Key.ToByteArray());
            if (storedValue != null)
            {
                var record = new Record
                {
                    Key = request.Key,
                    Value = ByteString.CopyFrom(storedValue.Value),
                    TimeReceived = DateTimeOffset.FromUnixTimeSeconds(storedValue.Timestamp).ToString("o")
                };
                return MessageHelper.CreateGetValueResponse(record);
            }
            return MessageHelper.CreateGetValueResponse();
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling GetValue: {Error}", ex.Message);
            return MessageHelper.CreateGetValueResponse();
        }
    }

    private static async Task<Message> HandleAddProvider(Message request, KadDhtOptions options,
        IProviderStore providerStore, ILoggerFactory? loggerFactory)
    {
        try
        {
            if (options.Mode != KadDhtMode.Server)
                return new Message { Type = Message.Types.MessageType.AddProvider };

            foreach (var wirePeer in request.ProviderPeers)
            {
                var providerRecord = new ProviderRecord
                {
                    PeerId = new PeerId(wirePeer.Id.ToByteArray()),
                    Multiaddrs = wirePeer.Addrs.Select(a =>
                    {
                        try { return Multiformats.Address.Multiaddress.Decode(a.ToByteArray()).ToString(); }
                        catch { return ""; }
                    }).Where(s => !string.IsNullOrEmpty(s)).ToArray(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ttl = options.RecordTtl
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

    private static async Task<Message> HandleGetProviders(Message request, IProviderStore providerStore, ILoggerFactory? loggerFactory)
    {
        try
        {
            var providers = await providerStore.GetProvidersAsync(request.Key.ToByteArray(), 20);
            var response = MessageHelper.CreateGetProvidersResponse(
                providerPeers: providers.Select(p =>
                {
                    return new Integration.DhtNode
                    {
                        PeerId = p.PeerId,
                        PublicKey = new PublicKey(p.PeerId.Bytes),
                        Multiaddrs = p.Multiaddrs
                    };
                }));
            return response;
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling GetProviders: {Error}", ex.Message);
            return MessageHelper.CreateGetProvidersResponse();
        }
    }
}
