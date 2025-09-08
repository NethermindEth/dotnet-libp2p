// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// In-memory implementation of IProviderStore using thread-safe collections.
/// Suitable for testing and lightweight deployments.
/// </summary>
public class InMemoryProviderStore : IProviderStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ProviderRecord>> _providers;
    private readonly ILogger<InMemoryProviderStore>? _logger;
    private readonly int _maxProvidersPerKey;
    private readonly SemaphoreSlim _cleanupSemaphore;

    public InMemoryProviderStore(int maxProvidersPerKey = 20, ILoggerFactory? loggerFactory = null)
    {
        _providers = new ConcurrentDictionary<string, ConcurrentDictionary<string, ProviderRecord>>();
        _maxProvidersPerKey = maxProvidersPerKey;
        _logger = loggerFactory?.CreateLogger<InMemoryProviderStore>();
        _cleanupSemaphore = new SemaphoreSlim(1, 1);
    }

    public int TotalProviderCount => _providers.Values.Sum(providerSet => providerSet.Count);

    public int KeyCount => _providers.Count;

    public Task<bool> AddProviderAsync(ReadOnlyMemory<byte> key, ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);
        string peerIdString = provider.PeerId.ToString();

        // Get or create the provider set for this key
        var providerSet = _providers.GetOrAdd(keyString, _ => new ConcurrentDictionary<string, ProviderRecord>());

        // Check capacity limits
        if (providerSet.Count >= _maxProvidersPerKey && !providerSet.ContainsKey(peerIdString))
        {
            // Remove oldest provider to make room (simple FIFO eviction)
            var oldestEntry = providerSet.OrderBy(kvp => kvp.Value.StoredAt).FirstOrDefault();
            if (!oldestEntry.Equals(default(KeyValuePair<string, ProviderRecord>)))
            {
                providerSet.TryRemove(oldestEntry.Key, out _);
                _logger?.LogDebug("Evicted oldest provider {OldestProvider} for key {Key} to make room for {NewProvider}",
                    oldestEntry.Key, keyString[..Math.Min(keyString.Length, 16)] + "...", peerIdString);
            }
        }

        // Add or update the provider
        providerSet.AddOrUpdate(peerIdString, provider, (_, existingProvider) =>
        {
            // Replace if the new provider record is newer
            if (provider.Timestamp > existingProvider.Timestamp)
            {
                _logger?.LogDebug("Updated provider {Provider} for key {Key} with newer timestamp {Timestamp}",
                    peerIdString, keyString[..Math.Min(keyString.Length, 16)] + "...", provider.Timestamp);
                return provider;
            }

            _logger?.LogDebug("Kept existing provider {Provider} for key {Key} (newer timestamp {ExistingTimestamp} vs {NewTimestamp})",
                peerIdString, keyString[..Math.Min(keyString.Length, 16)] + "...", existingProvider.Timestamp, provider.Timestamp);
            return existingProvider;
        });

        _logger?.LogTrace("Added provider {Provider} for key {Key}", peerIdString, keyString[..Math.Min(keyString.Length, 16)] + "...");
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ProviderRecord>> GetProvidersAsync(ReadOnlyMemory<byte> key, int maxCount = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);

        if (!_providers.TryGetValue(keyString, out var providerSet))
        {
            _logger?.LogTrace("No providers found for key {Key}", keyString[..Math.Min(keyString.Length, 16)] + "...");
            return Task.FromResult<IReadOnlyList<ProviderRecord>>(Array.Empty<ProviderRecord>());
        }

        var validProviders = new List<ProviderRecord>();
        var expiredProviders = new List<string>();

        // Filter out expired providers and collect valid ones
        foreach (var kvp in providerSet)
        {
            if (kvp.Value.IsExpired)
            {
                expiredProviders.Add(kvp.Key);
            }
            else
            {
                validProviders.Add(kvp.Value);
            }
        }

        // Remove expired providers
        foreach (string expiredPeerId in expiredProviders)
        {
            providerSet.TryRemove(expiredPeerId, out _);
        }

        if (expiredProviders.Count > 0)
        {
            _logger?.LogDebug("Removed {ExpiredCount} expired providers for key {Key}",
                expiredProviders.Count, keyString[..Math.Min(keyString.Length, 16)] + "...");
        }

        // Apply limit and return
        var result = validProviders
            .OrderByDescending(p => p.Timestamp) // Return most recent providers first
            .Take(Math.Min(maxCount, validProviders.Count))
            .ToArray();

        _logger?.LogTrace("Retrieved {ProviderCount} providers for key {Key}",
            result.Length, keyString[..Math.Min(keyString.Length, 16)] + "...");

        return Task.FromResult<IReadOnlyList<ProviderRecord>>(result);
    }

    public Task<bool> RemoveProviderAsync(ReadOnlyMemory<byte> key, PeerId peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);
        string peerIdString = peerId.ToString();

        if (_providers.TryGetValue(keyString, out var providerSet))
        {
            bool removed = providerSet.TryRemove(peerIdString, out _);
            
            // Clean up empty provider sets
            if (providerSet.IsEmpty)
            {
                _providers.TryRemove(keyString, out _);
            }

            if (removed)
            {
                _logger?.LogDebug("Removed provider {Provider} for key {Key}", peerIdString, keyString[..Math.Min(keyString.Length, 16)] + "...");
            }

            return Task.FromResult(removed);
        }

        return Task.FromResult(false);
    }

    public Task<int> RemoveAllProvidersAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);

        if (_providers.TryRemove(keyString, out var providerSet))
        {
            int removedCount = providerSet.Count;
            _logger?.LogDebug("Removed all {RemovedCount} providers for key {Key}", removedCount, keyString[..Math.Min(keyString.Length, 16)] + "...");
            return Task.FromResult(removedCount);
        }

        return Task.FromResult(0);
    }

    public Task<bool> HasProvidersAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string keyString = Convert.ToBase64String(key.Span);

        if (_providers.TryGetValue(keyString, out var providerSet))
        {
            // Check if there are any non-expired providers
            var hasValidProviders = providerSet.Values.Any(p => !p.IsExpired);
            
            // Clean up if all providers are expired
            if (!hasValidProviders)
            {
                _providers.TryRemove(keyString, out _);
            }

            return Task.FromResult(hasValidProviders);
        }

        return Task.FromResult(false);
    }

    public async Task<int> CleanupExpiredProvidersAsync(CancellationToken cancellationToken = default)
    {
        // Use semaphore to prevent concurrent cleanup operations
        await _cleanupSemaphore.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            int totalRemovedCount = 0;
            var keysToRemove = new List<string>();

            foreach (var keyProvidersPair in _providers)
            {
                var expiredPeerIds = new List<string>();
                
                // Find expired providers for this key
                foreach (var providerPair in keyProvidersPair.Value)
                {
                    if (providerPair.Value.IsExpired)
                    {
                        expiredPeerIds.Add(providerPair.Key);
                    }
                }

                // Remove expired providers
                foreach (string expiredPeerId in expiredPeerIds)
                {
                    if (keyProvidersPair.Value.TryRemove(expiredPeerId, out _))
                    {
                        totalRemovedCount++;
                    }
                }

                // Mark empty keys for removal
                if (keyProvidersPair.Value.IsEmpty)
                {
                    keysToRemove.Add(keyProvidersPair.Key);
                }
            }

            // Remove empty key entries
            foreach (string emptyKey in keysToRemove)
            {
                _providers.TryRemove(emptyKey, out _);
            }

            if (totalRemovedCount > 0)
            {
                _logger?.LogInformation("Cleaned up {RemovedCount} expired provider records from store", totalRemovedCount);
            }

            return totalRemovedCount;
        }
        finally
        {
            _cleanupSemaphore.Release();
        }
    }

    public IEnumerable<ReadOnlyMemory<byte>> GetAllKeys()
    {
        return _providers.Keys.Select(k => new ReadOnlyMemory<byte>(Convert.FromBase64String(k)));
    }
}