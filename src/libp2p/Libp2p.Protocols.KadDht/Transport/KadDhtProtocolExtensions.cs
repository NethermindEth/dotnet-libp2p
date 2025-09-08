// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using Nethermind.Libp2p.Protocols;
using Microsoft.Extensions.Logging;
using Google.Protobuf; // for ByteString

namespace Libp2p.Protocols.KadDht;

public readonly record struct TestNode(PeerId Id);

public static class KadDhtProtocolExtensions
{
    public const string DefaultBaseId = "/ipfs/kad/1.0.0";

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

        string pingId = baseId + "/ping";
        string findId = baseId + "/findneighbours";
        string putValueId = baseId + "/putvalue";
        string getValueId = baseId + "/getvalue";
        string addProviderId = baseId + "/addprovider";
        string getProvidersId = baseId + "/getproviders";

        // Ping protocol
        builder = builder.AddRequestResponseProtocol<PingRequest, PingResponse>(
            pingId,
            async (req, ctx) => await Task.FromResult(new PingResponse()),
            isExposed: isExposed);

        // FindNeighbours protocol
        builder = builder.AddRequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>(
            findId,
            async (req, ctx) =>
            {
                PublicKey target = new(req.Target.Value.ToByteArray());
                TestNode[] neighbours = [.. findNearest(target)];
                var resp = new FindNeighboursResponse();
                foreach (TestNode n in neighbours)
                {
                    resp.Neighbours.Add(new Node
                    {
                        PublicKey = ByteString.CopyFrom(n.Id.Bytes.ToArray()),
                    });
                }
                return await Task.FromResult(resp);
            },
            isExposed: isExposed);

        // PutValue protocol (only active in server mode)
        if (dhtOptions.Mode == KadDhtMode.Server)
        {
            builder = builder.AddRequestResponseProtocol<PutValueRequest, PutValueResponse>(
                putValueId,
                async (req, ctx) =>
                {
                    try
                    {
                        if (req.Value.Length > dhtOptions.MaxValueSize)
                        {
                            return new PutValueResponse { Success = false, Error = "Value too large" };
                        }

                        var storedValue = new StoredValue
                        {
                            Value = req.Value.ToByteArray(),
                            Signature = req.Signature.ToByteArray(),
                            Timestamp = req.Timestamp,
                            Publisher = req.Publisher.IsEmpty ? null : new PeerId(req.Publisher.ToByteArray()),
                            Ttl = dhtOptions.RecordTtl
                        };

                        bool stored = await dhtValueStore.PutValueAsync(req.Key.ToByteArray(), storedValue);
                        return new PutValueResponse { Success = stored };
                    }
                    catch (Exception ex)
                    {
                        loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                            ?.LogError(ex, "Error handling PutValue request: {ErrorMessage}", ex.Message);
                        return new PutValueResponse { Success = false, Error = ex.Message };
                    }
                },
                isExposed: isExposed);
        }

        // GetValue protocol
        builder = builder.AddRequestResponseProtocol<GetValueRequest, GetValueResponse>(
            getValueId,
            async (req, ctx) =>
            {
                try
                {
                    var storedValue = await dhtValueStore.GetValueAsync(req.Key.ToByteArray());
                    if (storedValue != null)
                    {
                        return new GetValueResponse
                        {
                            Found = true,
                            Value = Google.Protobuf.ByteString.CopyFrom(storedValue.Value),
                            Signature = storedValue.Signature != null ? Google.Protobuf.ByteString.CopyFrom(storedValue.Signature) : Google.Protobuf.ByteString.Empty,
                            Timestamp = storedValue.Timestamp,
                            Publisher = storedValue.Publisher != null ? Google.Protobuf.ByteString.CopyFrom(storedValue.Publisher.Bytes.ToArray()) : Google.Protobuf.ByteString.Empty
                        };
                    }
                    else
                    {
                        return new GetValueResponse { Found = false };
                    }
                }
                catch (Exception ex)
                {
                    loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                        ?.LogError(ex, "Error handling GetValue request: {ErrorMessage}", ex.Message);
                    return new GetValueResponse { Found = false };
                }
            },
            isExposed: isExposed);

        // AddProvider protocol (only active in server mode)
        if (dhtOptions.Mode == KadDhtMode.Server)
        {
            builder = builder.AddRequestResponseProtocol<AddProviderRequest, AddProviderResponse>(
                addProviderId,
                async (req, ctx) =>
                {
                    try
                    {
                        var providerRecord = new ProviderRecord
                        {
                            PeerId = new PeerId(req.ProviderId.ToByteArray()),
                            Multiaddrs = req.Multiaddrs.ToArray(),
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            Ttl = dhtOptions.RecordTtl
                        };

                        bool added = await dhtProviderStore.AddProviderAsync(req.Key.ToByteArray(), providerRecord);
                        return new AddProviderResponse { Success = added };
                    }
                    catch (Exception ex)
                    {
                        loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                            ?.LogError(ex, "Error handling AddProvider request: {ErrorMessage}", ex.Message);
                        return new AddProviderResponse { Success = false, Error = ex.Message };
                    }
                },
                isExposed: isExposed);
        }

        // GetProviders protocol
        builder = builder.AddRequestResponseProtocol<GetProvidersRequest, GetProvidersResponse>(
            getProvidersId,
            async (req, ctx) =>
            {
                try
                {
                    var providers = await dhtProviderStore.GetProvidersAsync(req.Key.ToByteArray(), req.Count);
                    var response = new GetProvidersResponse();
                    
                    foreach (var provider in providers)
                    {
                        response.Providers.Add(new Provider
                        {
                            PeerId = Google.Protobuf.ByteString.CopyFrom(provider.PeerId.Bytes.ToArray()),
                            Multiaddrs = { provider.Multiaddrs },
                            Timestamp = provider.Timestamp
                        });
                    }

                    return response;
                }
                catch (Exception ex)
                {
                    loggerFactory?.CreateLogger("KadDhtProtocolExtensions")
                        ?.LogError(ex, "Error handling GetProviders request: {ErrorMessage}", ex.Message);
                    return new GetProvidersResponse();
                }
            },
            isExposed: isExposed);

        return builder;
    }
}
