// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// In-memory implementation of IValueStore using thread-safe collections.
/// Suitable for testing and lightweight deployments.
/// </summary>
public class InMemoryValueStore : IValueStore
{
    private readonly ConcurrentDictionary<string, StoredValue> _values;
    private readonly ILogger<InMemoryValueStore>? _logger;
    private readonly int _maxValues;
    private readonly SemaphoreSlim _cleanupSemaphore;

    public InMemoryValueStore(int maxValues = 1000, ILoggerFactory? loggerFactory = null)
    {
        _values = new ConcurrentDictionary<string, StoredValue>();
        _maxValues = maxValues;
        _logger = loggerFactory?.CreateLogger<InMemoryValueStore>();
        _cleanupSemaphore = new SemaphoreSlim(1, 1);
    }

    public int Count => _values.Count;

    public Task<bool> PutValueAsync(ReadOnlyMemory<byte> key, StoredValue value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);
        
        // Check capacity and enforce limits
        if (_values.Count >= _maxValues && !_values.ContainsKey(keyString))
        {
            _logger?.LogWarning("Value store at capacity ({MaxValues}), cannot store new value for key {Key}", 
                _maxValues, keyString[..Math.Min(keyString.Length, 16)] + "...");
            return Task.FromResult(false);
        }

        // Validate value size and content
        if (value.Value.Length == 0)
        {
            _logger?.LogWarning("Attempted to store empty value for key {Key}", keyString[..Math.Min(keyString.Length, 16)] + "...");
            return Task.FromResult(false);
        }

        _values.AddOrUpdate(keyString, value, (_, existingValue) =>
        {
            // Replace if the new value is newer (higher timestamp)
            if (value.Timestamp > existingValue.Timestamp)
            {
                _logger?.LogDebug("Updated value for key {Key} with newer timestamp {Timestamp}", 
                    keyString[..Math.Min(keyString.Length, 16)] + "...", value.Timestamp);
                return value;
            }
            
            _logger?.LogDebug("Kept existing value for key {Key} (newer timestamp {ExistingTimestamp} vs {NewTimestamp})", 
                keyString[..Math.Min(keyString.Length, 16)] + "...", existingValue.Timestamp, value.Timestamp);
            return existingValue;
        });

        return Task.FromResult(true);
    }

    public Task<StoredValue?> GetValueAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);
        
        if (_values.TryGetValue(keyString, out StoredValue? value))
        {
            if (value.IsExpired)
            {
                _logger?.LogDebug("Value for key {Key} has expired, removing", keyString[..Math.Min(keyString.Length, 16)] + "...");
                _values.TryRemove(keyString, out _);
                return Task.FromResult<StoredValue?>(null);
            }
            
            _logger?.LogTrace("Retrieved value for key {Key}", keyString[..Math.Min(keyString.Length, 16)] + "...");
            return Task.FromResult<StoredValue?>(value);
        }

        _logger?.LogTrace("Value not found for key {Key}", keyString[..Math.Min(keyString.Length, 16)] + "...");
        return Task.FromResult<StoredValue?>(null);
    }

    public Task<bool> HasValueAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);
        
        if (_values.TryGetValue(keyString, out StoredValue? value))
        {
            if (value.IsExpired)
            {
                _values.TryRemove(keyString, out _);
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> RemoveValueAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);
        bool removed = _values.TryRemove(keyString, out _);
        
        if (removed)
        {
            _logger?.LogDebug("Removed value for key {Key}", keyString[..Math.Min(keyString.Length, 16)] + "...");
        }

        return Task.FromResult(removed);
    }

    public async Task<int> CleanupExpiredValuesAsync(CancellationToken cancellationToken = default)
    {
        // Use semaphore to prevent concurrent cleanup operations
        await _cleanupSemaphore.WaitAsync(cancellationToken);
        
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var expiredKeys = new List<string>();
            var now = DateTime.UtcNow;

            // Find expired values
            foreach (var kvp in _values)
            {
                if (kvp.Value.StoredAt.Add(kvp.Value.Ttl) <= now)
                {
                    expiredKeys.Add(kvp.Key);
                }
            }

            // Remove expired values
            int removedCount = 0;
            foreach (string expiredKey in expiredKeys)
            {
                if (_values.TryRemove(expiredKey, out _))
                {
                    removedCount++;
                }
            }

            if (removedCount > 0)
            {
                _logger?.LogInformation("Cleaned up {RemovedCount} expired values from store", removedCount);
            }

            return removedCount;
        }
        finally
        {
            _cleanupSemaphore.Release();
        }
    }

    public IEnumerable<ReadOnlyMemory<byte>> GetAllKeys()
    {
        return _values.Keys.Select(k => new ReadOnlyMemory<byte>(Convert.FromBase64String(k)));
    }
}