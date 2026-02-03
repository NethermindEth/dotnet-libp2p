// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Represents a stored value in the DHT.
/// </summary>
public sealed record DhtValue
{
    public required byte[] Key { get; init; }
    public required byte[] Value { get; init; }
    public byte[]? Signature { get; init; }
    public long Timestamp { get; init; }
    public byte[]? Publisher { get; init; }
    public DateTime StoredAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
}

/// <summary>
/// Distributed value store for DHT. Stores key-value pairs with TTL and metadata.
/// Thread-safe for concurrent access.
/// </summary>
public sealed class DhtValueStore
{
    private readonly ConcurrentDictionary<string, DhtValue> _store = new();
    private readonly ILogger<DhtValueStore> _logger;
    private readonly TimeSpan _defaultTtl;
    private readonly System.Threading.Timer _cleanupTimer;

    public DhtValueStore(ILoggerFactory? loggerFactory = null, TimeSpan? defaultTtl = null)
    {
        _logger = loggerFactory?.CreateLogger<DhtValueStore>() ?? NullLogger<DhtValueStore>.Instance;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(24);
        
        // Run cleanup every 5 minutes
        _cleanupTimer = new System.Threading.Timer(CleanupExpiredValues, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Store or update a value in the DHT.
    /// </summary>
    public bool Put(byte[] key, byte[] value, byte[]? signature = null, long? timestamp = null, byte[]? publisher = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var keyStr = Convert.ToBase64String(key);
        var now = DateTime.UtcNow;
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var dhtValue = new DhtValue
        {
            Key = key,
            Value = value,
            Signature = signature,
            Timestamp = ts,
            Publisher = publisher,
            StoredAt = now,
            ExpiresAt = now.Add(_defaultTtl)
        };

        // Check if we should update (newer timestamp wins)
        if (_store.TryGetValue(keyStr, out var existing))
        {
            if (ts <= existing.Timestamp)
            {
                _logger.LogDebug("Ignoring PUT for key {Key} - existing value is newer (ts: {ExistingTs} vs {NewTs})",
                    keyStr.Substring(0, Math.Min(16, keyStr.Length)), existing.Timestamp, ts);
                return false;
            }
        }

        _store[keyStr] = dhtValue;
        _logger.LogInformation("Stored value for key {Key} (ts: {Timestamp}, expires: {Expires})",
            keyStr.Substring(0, Math.Min(16, keyStr.Length)), ts, dhtValue.ExpiresAt);
        return true;
    }

    /// <summary>
    /// Retrieve a value from the DHT.
    /// </summary>
    public DhtValue? Get(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var keyStr = Convert.ToBase64String(key);
        if (_store.TryGetValue(keyStr, out var value))
        {
            // Check if expired
            if (value.ExpiresAt < DateTime.UtcNow)
            {
                _store.TryRemove(keyStr, out _);
                _logger.LogDebug("Key {Key} expired, removed from store", keyStr.Substring(0, Math.Min(16, keyStr.Length)));
                return null;
            }

            _logger.LogDebug("Found value for key {Key}", keyStr.Substring(0, Math.Min(16, keyStr.Length)));
            return value;
        }

        _logger.LogDebug("Key {Key} not found in store", keyStr.Substring(0, Math.Min(16, keyStr.Length)));
        return null;
    }

    /// <summary>
    /// Check if a key exists in the store (and is not expired).
    /// </summary>
    public bool Contains(byte[] key)
    {
        return Get(key) is not null;
    }

    /// <summary>
    /// Get the number of stored values.
    /// </summary>
    public int Count => _store.Count;

    /// <summary>
    /// Get all stored keys.
    /// </summary>
    public IEnumerable<byte[]> GetAllKeys()
    {
        foreach (var kvp in _store)
        {
            if (kvp.Value.ExpiresAt >= DateTime.UtcNow)
            {
                yield return kvp.Value.Key;
            }
        }
    }

    /// <summary>
    /// Remove a value from the store.
    /// </summary>
    public bool Remove(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var keyStr = Convert.ToBase64String(key);
        return _store.TryRemove(keyStr, out _);
    }

    /// <summary>
    /// Clear all values from the store.
    /// </summary>
    public void Clear()
    {
        _store.Clear();
        _logger.LogInformation("Cleared all values from store");
    }

    private void CleanupExpiredValues(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredKeys = _store
            .Where(kvp => kvp.Value.ExpiresAt < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _store.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired values", expiredKeys.Count);
        }
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }
}
