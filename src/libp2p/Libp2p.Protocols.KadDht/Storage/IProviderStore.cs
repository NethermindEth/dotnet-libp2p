// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Represents a provider record with metadata.
/// </summary>
public record ProviderRecord
{
    /// <summary>
    /// The PeerId of the provider.
    /// </summary>
    public required PeerId PeerId { get; init; }

    /// <summary>
    /// Multiaddresses where the provider can be reached.
    /// </summary>
    public IReadOnlyList<string> Multiaddrs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Unix timestamp when the provider record was created.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// When this provider record was stored locally.
    /// </summary>
    public DateTime StoredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Time-to-live for this provider record.
    /// </summary>
    public TimeSpan Ttl { get; init; }

    /// <summary>
    /// Whether this provider record has expired based on TTL.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > StoredAt.Add(Ttl);
}

/// <summary>
/// Interface for storing and retrieving DHT provider records.
/// </summary>
public interface IProviderStore
{
    /// <summary>
    /// Add a provider for a given key.
    /// </summary>
    /// <param name="key">The key being provided.</param>
    /// <param name="provider">The provider record.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the provider was added successfully.</returns>
    Task<bool> AddProviderAsync(ReadOnlyMemory<byte> key, ProviderRecord provider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get providers for a given key.
    /// </summary>
    /// <param name="key">The key to find providers for.</param>
    /// <param name="maxCount">Maximum number of providers to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of provider records.</returns>
    Task<IReadOnlyList<ProviderRecord>> GetProvidersAsync(ReadOnlyMemory<byte> key, int maxCount = 20, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a specific provider for a key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="peerId">The provider's PeerId to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the provider was removed.</returns>
    Task<bool> RemoveProviderAsync(ReadOnlyMemory<byte> key, PeerId peerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove all providers for a key.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of providers that were removed.</returns>
    Task<int> RemoveAllProvidersAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if there are any providers for a given key.
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if there are providers for this key.</returns>
    Task<bool> HasProvidersAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove all expired provider records from the store.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of provider records that were removed.</returns>
    Task<int> CleanupExpiredProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the total number of provider records across all keys.
    /// </summary>
    /// <returns>The total count of provider records.</returns>
    int TotalProviderCount { get; }

    /// <summary>
    /// Get the number of keys that have providers.
    /// </summary>
    /// <returns>The count of keys with providers.</returns>
    int KeyCount { get; }

    /// <summary>
    /// Get all keys that have providers (for debugging/monitoring).
    /// </summary>
    /// <returns>Enumerable of all keys that have providers.</returns>
    IEnumerable<ReadOnlyMemory<byte>> GetAllKeys();
}