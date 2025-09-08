// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Represents a stored value with metadata for DHT operations.
/// </summary>
public record StoredValue
{
    /// <summary>
    /// The actual value data.
    /// </summary>
    public required byte[] Value { get; init; }

    /// <summary>
    /// Digital signature for value validation.
    /// </summary>
    public byte[]? Signature { get; init; }

    /// <summary>
    /// Unix timestamp when the value was originally published.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>
    /// PeerId of the original publisher.
    /// </summary>
    public PeerId? Publisher { get; init; }

    /// <summary>
    /// When this value was stored locally.
    /// </summary>
    public DateTime StoredAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Time-to-live for this value.
    /// </summary>
    public TimeSpan Ttl { get; init; }

    /// <summary>
    /// Whether this value has expired based on TTL.
    /// </summary>
    public bool IsExpired => DateTime.UtcNow > StoredAt.Add(Ttl);
}

/// <summary>
/// Interface for storing and retrieving DHT values.
/// </summary>
public interface IValueStore
{
    /// <summary>
    /// Store a value with the given key.
    /// </summary>
    /// <param name="key">The key to store the value under.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the value was stored successfully.</returns>
    Task<bool> PutValueAsync(ReadOnlyMemory<byte> key, StoredValue value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve a value by key.
    /// </summary>
    /// <param name="key">The key to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored value, or null if not found or expired.</returns>
    Task<StoredValue?> GetValueAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a value exists for the given key (and is not expired).
    /// </summary>
    /// <param name="key">The key to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the value exists and is not expired.</returns>
    Task<bool> HasValueAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a value by key.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the value was removed.</returns>
    Task<bool> RemoveValueAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove all expired values from the store.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of values that were removed.</returns>
    Task<int> CleanupExpiredValuesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the total number of stored values (including expired ones).
    /// </summary>
    /// <returns>The count of stored values.</returns>
    int Count { get; }

    /// <summary>
    /// Get all keys currently stored (for debugging/monitoring).
    /// </summary>
    /// <returns>Enumerable of all keys.</returns>
    IEnumerable<ReadOnlyMemory<byte>> GetAllKeys();
}