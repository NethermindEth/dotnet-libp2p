// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

public readonly record struct TestNode(PeerId Id)
{
    public Multiaddress[]? Addresses { get; init; }

    public bool Equals(TestNode other) => Id.Equals(other.Id);

    public override int GetHashCode() => Id.GetHashCode();
}

public static class KadDhtProtocolExtensions
{
    public const string DefaultBaseId = KadDhtOptions.DefaultProtocolId;

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
        WirePeerPolicy.Validate(dhtOptions);
        ProviderRecordLimits.Validate(dhtOptions.MaxProvidersPerKey, dhtOptions.MaxProviderKeys,
            dhtOptions.MaxAddressesPerProvider, dhtOptions.MaxProviderRecordBytes);
        var dhtValueStore = valueStore ?? new InMemoryValueStore(dhtOptions.MaxStoredValues, loggerFactory);
        var dhtProviderStore = providerStore ?? new InMemoryProviderStore(dhtOptions.MaxProvidersPerKey, dhtOptions.MaxProviderKeys, dhtOptions.MaxAddressesPerProvider, dhtOptions.MaxProviderRecordBytes, loggerFactory);
        var dhtValidator = validator ?? DefaultRecordValidator.Instance;

        builder = builder.AddRequestResponseProtocol<Message, Message>(
            async (request, ctx, token) =>
            {
                TryTrackPeer(ctx, onPeerSeen, dhtOptions);

                return request.Type switch
                {
                    Message.Types.MessageType.Ping => MessageHelper.CreatePingResponse(),

                    Message.Types.MessageType.FindNode => HandleFindNode(request, findNearest, ctx, dhtOptions),

                    Message.Types.MessageType.PutValue => await HandlePutValue(request, dhtOptions, dhtValueStore, dhtValidator, loggerFactory, token),

                    Message.Types.MessageType.GetValue => await HandleGetValue(request, dhtOptions, dhtValueStore, findNearest, loggerFactory, ctx, token),

                    Message.Types.MessageType.AddProvider => await HandleAddProvider(request, ctx, dhtOptions, dhtProviderStore, loggerFactory, token),

                    Message.Types.MessageType.GetProviders => await HandleGetProviders(request, dhtOptions, dhtProviderStore, findNearest, loggerFactory, ctx, token),

                    _ => new Message()
                };
            },
            baseId,
            maxMessageSize: dhtOptions.MaxMessageSize,
            isExposed: isExposed,
            expectsResponse: request => request.Type != Message.Types.MessageType.AddProvider);

        return builder;
    }

    private static Message HandleFindNode(Message request, Func<PublicKey, IEnumerable<DhtNode>> findNearest, ISessionContext ctx, KadDhtOptions options)
    {
        if (!request.HasKey) return MessageHelper.CreateFindNodeResponse(Enumerable.Empty<DhtNode>());
        var target = new PublicKey(request.Key.ToByteArray());
        var remotePeerId = ctx.State.RemoteAddress?.GetPeerId();
        // The requester still consumes an inspection slot. Filtering before Take would
        // allow an unbounded custom callback prefix containing only requester entries.
        var neighbours = findNearest(target).Take(options.KSize)
            .Where(n => remotePeerId == null || !n.PeerId.Equals(remotePeerId));
        return MessageHelper.CreatePeerResponse(Message.Types.MessageType.FindNode, options, neighbours, connection: CreateConnectionResolver(ctx));
    }

    private static async Task<Message> HandlePutValue(Message request, KadDhtOptions options,
        IValueStore valueStore, IRecordValidator validator, ILoggerFactory? loggerFactory, CancellationToken token)
    {
        try
        {
            if (options.Mode != KadDhtMode.Server)
                return MessageHelper.CreatePutValueResponse();

            if (request.Record == null || request.Record.Value.Length > options.MaxValueSize || !request.Key.Equals(request.Record.Key))
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

            bool stored = await valueStore.PutValueAsync(key, storedValue, token);
            token.ThrowIfCancellationRequested();
            return MessageHelper.CreatePutValueResponse(stored ? request.Record : null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling PutValue: {Error}", ex.Message);
            return MessageHelper.CreatePutValueResponse();
        }
    }

    private static async Task<Message> HandleGetValue(Message request, KadDhtOptions options, IValueStore valueStore,
        Func<PublicKey, IEnumerable<DhtNode>> findNearest, ILoggerFactory? loggerFactory, ISessionContext ctx, CancellationToken token)
    {
        try
        {
            var target = new PublicKey(request.Key.ToByteArray());
            var closerPeers = findNearest(target);

            var storedValue = await valueStore.GetValueAsync(request.Key.ToByteArray(), token);
            if (storedValue != null)
            {
                var record = new Record
                {
                    Key = request.Key,
                    Value = ByteString.CopyFrom(storedValue.Value),
                    TimeReceived = DateTimeOffset.FromUnixTimeSeconds(storedValue.Timestamp).ToString("o")
                };
                return MessageHelper.CreatePeerResponse(Message.Types.MessageType.GetValue, options, closerPeers, record: record, connection: CreateConnectionResolver(ctx));
            }
            return MessageHelper.CreatePeerResponse(Message.Types.MessageType.GetValue, options, closerPeers, connection: CreateConnectionResolver(ctx));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling GetValue: {Error}", ex.Message);
            return MessageHelper.CreateGetValueResponse();
        }
    }

    private static async Task<Message> HandleAddProvider(Message request, ISessionContext ctx, KadDhtOptions options,
        IProviderStore providerStore, ILoggerFactory? loggerFactory, CancellationToken token)
    {
        try
        {
            if (options.Mode != KadDhtMode.Server)
                return new Message { Type = Message.Types.MessageType.AddProvider };

            var senderPeerId = ctx.State.RemoteAddress?.GetPeerId();

            foreach (var wirePeer in request.ProviderPeers.Take(options.MaxProvidersPerKey))
            {
                if (!WirePeerPolicy.IsValidIdentity(wirePeer.Id.Span, options.MaxPeerIdBytes)) continue;
                if (senderPeerId is not null && !wirePeer.Id.Span.SequenceEqual(senderPeerId.Bytes))
                {
                    var logger = loggerFactory?.CreateLogger("KadDhtProtocolExtensions");
                    // Id validation and the warning gate precede identity copies/formatting.
                    if (logger?.IsEnabled(LogLevel.Warning) == true)
                        logger.LogWarning("ADD_PROVIDER rejected: provider PeerId {ProviderId} does not match sender {SenderId}",
                            new PeerId(wirePeer.Id.ToByteArray()), senderPeerId);
                    continue;
                }
                if (!ProviderRecordLimits.CanFitIdentity(request.Key.Length, wirePeer.Id.Length, options.MaxProviderRecordBytes))
                    continue;

                var providerPeerId = new PeerId(wirePeer.Id.ToByteArray());
                if (!ProviderRecordLimits.TryGetAddressBudget(request.Key.Length, wirePeer.Id.Length,
                    providerPeerId.ToString(), options.MaxProviderRecordBytes, out long remaining))
                    continue;

                var addresses = new List<string>();
                for (int i = 0; i < Math.Min(wirePeer.Addrs.Count, options.MaxAddressesPerProvider); i++)
                {
                    var encoded = wirePeer.Addrs[i];
                    // Bound work and allocation before copying or invoking the address parser.
                    if (encoded.Length > remaining) continue;
                    try
                    {
                        var multiaddress = Multiaddress.Decode(encoded.ToByteArray());
                        var destination = multiaddress.GetPeerId();
                        if (destination is not null && !destination.Equals(providerPeerId)) continue;
                        string address = multiaddress.ToString();
                        if (!string.IsNullOrEmpty(address) && ProviderRecordLimits.TryRetainAddress(address, ref remaining))
                            addresses.Add(address);
                    }
                    catch
                    {
                        // Malformed addresses consume their inspection slot but are not retained.
                    }
                }

                var providerRecord = new ProviderRecord
                {
                    PeerId = providerPeerId,
                    Multiaddrs = addresses.AsReadOnly(),
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Ttl = options.ProviderRecordTtl
                };

                await providerStore.AddProviderAsync(request.Key.ToByteArray(), providerRecord, token);
            }

            return new Message { Type = Message.Types.MessageType.AddProvider };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling AddProvider: {Error}", ex.Message);
            return new Message { Type = Message.Types.MessageType.AddProvider };
        }
    }

    private static async Task<Message> HandleGetProviders(Message request, KadDhtOptions options, IProviderStore providerStore,
        Func<PublicKey, IEnumerable<DhtNode>> findNearest, ILoggerFactory? loggerFactory, ISessionContext ctx, CancellationToken token)
    {
        try
        {
            var providers = await providerStore.GetProvidersAsync(request.Key.ToByteArray(), options.MaxProvidersPerKey, token);
            var target = new PublicKey(request.Key.ToByteArray());
            var response = MessageHelper.CreatePeerResponse(Message.Types.MessageType.GetProviders, options,
                providerPeers: providers.Take(options.MaxProvidersPerKey).Where(p => WirePeerPolicy.IsValidIdentity(p.PeerId.Bytes, options.MaxPeerIdBytes)).Select(p => new Integration.DhtNode
                {
                    PeerId = p.PeerId,
                    PublicKey = new PublicKey(p.PeerId.Bytes),
                    Multiaddrs = p.Multiaddrs
                }),
                closerPeers: findNearest(target), connection: CreateConnectionResolver(ctx));
            return response;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                ?.LogError(ex, "Error handling GetProviders: {Error}", ex.Message);
            return MessageHelper.CreateGetProvidersResponse();
        }
    }

    private static void TryTrackPeer(ISessionContext ctx, Action<DhtNode>? onPeerSeen, KadDhtOptions options)
    {
        if (onPeerSeen == null) return;
        var remotePeerId = ctx.State.RemoteAddress?.GetPeerId();
        if (remotePeerId == null || !WirePeerPolicy.IsValidIdentity(remotePeerId.Bytes, options.MaxPeerIdBytes)) return;
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

    private static Func<PeerId, Message.Types.ConnectionType> CreateConnectionResolver(ISessionContext context)
    {
        ILookup<PeerId, CancellationToken>? connected = null;
        return peerId =>
        {
            // Build only when a validated response peer needs a flag, and share the result
            // across both peer lists. Copy eligible sessions under their collection lock;
            // converting addresses to peer IDs must not hold up session admission/teardown.
            if (connected is null)
            {
                LocalPeer.Session[] sessions = [];
                if (context.Peer is LocalPeer local)
                {
                    lock (local.Sessions)
                    {
                        sessions = local.Sessions.Where(session => session.Connected.IsCompletedSuccessfully &&
                            !session.Closed.IsCancellationRequested).ToArray();
                    }
                }
                connected = sessions.Select(session => (PeerId: session.State.RemotePeerId, session.Closed))
                    .Where(session => session.PeerId is not null)
                    .ToLookup(session => session.PeerId!, session => session.Closed);
            }
            // A session can close after the snapshot, and another session for that same
            // identity can remain live. Check the retained closure tokens at each lookup.
            return connected[peerId].Any(closed => !closed.IsCancellationRequested)
                ? Message.Types.ConnectionType.Connected : Message.Types.ConnectionType.NotConnected;
        };
    }
}
