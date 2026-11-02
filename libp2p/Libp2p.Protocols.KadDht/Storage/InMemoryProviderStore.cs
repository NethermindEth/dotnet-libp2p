// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

[assembly: InternalsVisibleTo("Nethermind.Libp2p.Protocols.KadDht.Tests")]

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Bounded, thread-safe in-memory provider storage. New keys are rejected at capacity
/// unless a fully expired bucket can be reclaimed, independently of background maintenance.
/// </summary>
public class InMemoryProviderStore : IProviderStore
{
    private sealed record StoredProvider(ProviderRecord Record, DateTime ExpiresAt);

    // Buckets are prepared before publication and never mutated afterwards.
    private sealed class Bucket(string key, Dictionary<string, StoredProvider> providers)
    {
        internal string Key { get; } = key;
        internal Dictionary<string, StoredProvider> Providers { get; } = providers;
        internal DateTime ExpiresAt { get; } = providers.Values.Max(p => p.ExpiresAt);
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Bucket> _providers = new(StringComparer.Ordinal);
    private readonly SortedSet<Bucket> _expiryIndex = new(Comparer<Bucket>.Create((left, right) =>
    {
        int comparison = left.ExpiresAt.CompareTo(right.ExpiresAt);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Key, right.Key);
    }));
    private readonly ILogger<InMemoryProviderStore>? _logger;
    private readonly int _maxProvidersPerKey;
    private readonly int _maxProviderKeys;
    private readonly int _maxAddressesPerProvider;
    private readonly int _maxProviderRecordBytes;
    private readonly TimeProvider _timeProvider;

    public InMemoryProviderStore(int maxProvidersPerKey = 20, ILoggerFactory? loggerFactory = null)
        : this(maxProvidersPerKey, ProviderRecordLimits.DefaultMaxProviderKeys,
            ProviderRecordLimits.DefaultMaxAddressesPerProvider, ProviderRecordLimits.DefaultMaxProviderRecordBytes, loggerFactory)
    {
    }

    public InMemoryProviderStore(int maxProvidersPerKey, int maxProviderKeys, int maxAddressesPerProvider,
        int maxProviderRecordBytes, ILoggerFactory? loggerFactory = null)
        : this(maxProvidersPerKey, maxProviderKeys, maxAddressesPerProvider, maxProviderRecordBytes, TimeProvider.System, loggerFactory)
    {
    }

    internal InMemoryProviderStore(int maxProvidersPerKey, int maxProviderKeys, int maxAddressesPerProvider,
        int maxProviderRecordBytes, TimeProvider timeProvider, ILoggerFactory? loggerFactory = null)
    {
        ProviderRecordLimits.Validate(maxProvidersPerKey, maxProviderKeys, maxAddressesPerProvider, maxProviderRecordBytes);
        _maxProvidersPerKey = maxProvidersPerKey;
        _maxProviderKeys = maxProviderKeys;
        _maxAddressesPerProvider = maxAddressesPerProvider;
        _maxProviderRecordBytes = maxProviderRecordBytes;
        _timeProvider = timeProvider;
        _logger = loggerFactory?.CreateLogger<InMemoryProviderStore>();
    }

    public int TotalProviderCount
    {
        get { lock (_sync) return _providers.Values.Sum(bucket => bucket.Providers.Count); }
    }

    public int KeyCount
    {
        get { lock (_sync) return _providers.Count; }
    }

    internal int ExpiryIndexCount
    {
        get { lock (_sync) return _expiryIndex.Count; }
    }

    public Task<bool> AddProviderAsync(ReadOnlyMemory<byte> key, ProviderRecord provider, CancellationToken cancellationToken = default)
    {
        // Preserve the synchronous in-memory API's behavior with pre-canceled tokens.
        if (!ProviderRecordLimits.CanFitIdentity(key.Length, provider.PeerId.Bytes.Length, _maxProviderRecordBytes))
            return Task.FromResult(false);

        string peerIdString = provider.PeerId.ToString();
        if (!ProviderRecordLimits.TryGetAddressBudget(key.Length, provider.PeerId.Bytes.Length, peerIdString,
            _maxProviderRecordBytes, out long remaining))
            return Task.FromResult(false);

        var addresses = new List<string>();
        for (int i = 0; i < Math.Min(provider.Multiaddrs.Count, _maxAddressesPerProvider); i++)
        {
            string address = provider.Multiaddrs[i];
            if (ProviderRecordLimits.TryRetainAddress(address, ref remaining)) addresses.Add(address);
        }
        var snapshot = provider with { Multiaddrs = addresses.AsReadOnly() };
        var stored = new StoredProvider(snapshot, provider.StoredAt.Add(provider.Ttl));
        string keyString = Convert.ToBase64String(key.Span);

        lock (_sync)
        {
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            _providers.TryGetValue(keyString, out var previous);
            var providers = previous is null
                ? new Dictionary<string, StoredProvider>(StringComparer.Ordinal)
                : LiveProviders(previous, now);

            if (providers.TryGetValue(peerIdString, out var existing))
            {
                if (provider.Timestamp > existing.Record.Timestamp) providers[peerIdString] = stored;
            }
            else
            {
                if (providers.Count >= _maxProvidersPerKey) return Task.FromResult(false);
                providers.Add(peerIdString, stored);
            }

            var next = new Bucket(previous?.Key ?? keyString, providers);
            Bucket? reclaim = null;
            if (previous is null && _providers.Count >= _maxProviderKeys)
            {
                reclaim = _expiryIndex.Min;
                // A bucket is reclaimable only after its latest provider has expired.
                if (reclaim is null || now <= reclaim.ExpiresAt) return Task.FromResult(false);
            }

            if (reclaim is not null) RemoveBucket(reclaim);
            Publish(previous, next);
        }

        _logger?.LogTrace("Stored provider {Provider}", peerIdString);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ProviderRecord>> GetProvidersAsync(ReadOnlyMemory<byte> key, int maxCount = 20, CancellationToken cancellationToken = default)
    {
        string keyString = Convert.ToBase64String(key.Span);
        lock (_sync)
        {
            var bucket = Prune(keyString, _timeProvider.GetUtcNow().UtcDateTime);
            IReadOnlyList<ProviderRecord> result = bucket is null ? Array.Empty<ProviderRecord>() : bucket.Providers.Values
                .Select(p => p.Record).OrderByDescending(p => p.Timestamp).Take(maxCount).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> RemoveProviderAsync(ReadOnlyMemory<byte> key, PeerId peerId, CancellationToken cancellationToken = default)
    {
        string keyString = Convert.ToBase64String(key.Span);
        string peerIdString = peerId.ToString();
        lock (_sync)
        {
            if (!_providers.TryGetValue(keyString, out var bucket)) return Task.FromResult(false);
            var providers = new Dictionary<string, StoredProvider>(bucket.Providers, StringComparer.Ordinal);
            if (!providers.Remove(peerIdString)) return Task.FromResult(false);
            Publish(bucket, providers.Count == 0 ? null : new Bucket(bucket.Key, providers));
            return Task.FromResult(true);
        }
    }

    public Task<int> RemoveAllProvidersAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        string keyString = Convert.ToBase64String(key.Span);
        lock (_sync)
        {
            if (!_providers.TryGetValue(keyString, out var bucket)) return Task.FromResult(0);
            RemoveBucket(bucket);
            return Task.FromResult(bucket.Providers.Count);
        }
    }

    public Task<bool> HasProvidersAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
    {
        string keyString = Convert.ToBase64String(key.Span);
        lock (_sync)
            return Task.FromResult(Prune(keyString, _timeProvider.GetUtcNow().UtcDateTime) is not null);
    }

    public Task<int> CleanupExpiredProvidersAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
        int removed = 0;
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var bucket in _providers.Values.ToArray())
            {
                var next = Prune(bucket.Key, now);
                removed += bucket.Providers.Count - (next?.Providers.Count ?? 0);
            }
        }
        _logger?.LogDebug("Cleaned up {RemovedCount} expired provider records", removed);
        return Task.FromResult(removed);
    }

    public IEnumerable<ReadOnlyMemory<byte>> GetAllKeys()
    {
        lock (_sync)
            return _providers.Keys.Select(k => new ReadOnlyMemory<byte>(Convert.FromBase64String(k))).ToArray();
    }

    private static Dictionary<string, StoredProvider> LiveProviders(Bucket bucket, DateTime now) =>
        bucket.Providers.Where(p => now <= p.Value.ExpiresAt).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private Bucket? Prune(string key, DateTime now)
    {
        if (!_providers.TryGetValue(key, out var previous)) return null;
        var providers = LiveProviders(previous, now);
        if (providers.Count == previous.Providers.Count) return previous;
        var next = providers.Count == 0 ? null : new Bucket(previous.Key, providers);
        Publish(previous, next);
        return next;
    }

    // All bucket and index changes happen together under _sync. Each key owns one index entry.
    private void Publish(Bucket? previous, Bucket? next)
    {
        if (previous is not null) RemoveBucket(previous);
        if (next is not null)
        {
            _providers.Add(next.Key, next);
            _expiryIndex.Add(next);
        }
    }

    private void RemoveBucket(Bucket bucket)
    {
        _providers.Remove(bucket.Key);
        _expiryIndex.Remove(bucket);
    }
}
