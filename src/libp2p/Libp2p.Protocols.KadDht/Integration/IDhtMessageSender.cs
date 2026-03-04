// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>
/// Result of a GET_VALUE RPC, which may return a value, closer peers, or both.
/// </summary>
public sealed record GetValueResult
{
    public byte[]? Value { get; init; }
    public long Timestamp { get; init; }
    public DhtNode[] CloserPeers { get; init; } = Array.Empty<DhtNode>();
    public bool HasValue => Value is { Length: > 0 };
}

/// <summary>
/// Result of a GET_PROVIDERS RPC, which returns providers and closer peers.
/// </summary>
public sealed record GetProvidersResult
{
    public DhtNode[] Providers { get; init; } = Array.Empty<DhtNode>();
    public DhtNode[] CloserPeers { get; init; } = Array.Empty<DhtNode>();
}

/// <summary>
/// Extended message sender interface for full DHT operations beyond basic Kademlia routing.
/// Adds PUT_VALUE, GET_VALUE, ADD_PROVIDER, and GET_PROVIDERS RPCs.
/// </summary>
public interface IDhtMessageSender : Kademlia.IKademliaMessageSender<PublicKey, DhtNode>
{
    Task<bool> PutValueAsync(DhtNode receiver, byte[] key, byte[] value, CancellationToken token = default);
    Task<GetValueResult> GetValueAsync(DhtNode receiver, byte[] key, CancellationToken token = default);
    Task AddProviderAsync(DhtNode receiver, byte[] key, DhtNode provider, CancellationToken token = default);
    Task<GetProvidersResult> GetProvidersAsync(DhtNode receiver, byte[] key, CancellationToken token = default);
}
