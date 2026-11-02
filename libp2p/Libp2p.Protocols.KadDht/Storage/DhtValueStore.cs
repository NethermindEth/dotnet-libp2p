// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.Storage;

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

/// <summary>Legacy synchronous facade over IValueStore. Prefer the asynchronous store API.</summary>
public sealed class DhtValueStore
{
    internal IValueStore Store { get; }
    internal TimeSpan DefaultTtl => _defaultTtl;
    private readonly TimeSpan _defaultTtl;

    public DhtValueStore(ILoggerFactory? loggerFactory = null, TimeSpan? defaultTtl = null, TimeSpan? cleanupInterval = null)
        : this(new InMemoryValueStore(int.MaxValue, loggerFactory), defaultTtl ?? TimeSpan.FromHours(24)) { }

    internal DhtValueStore(IValueStore store, TimeSpan defaultTtl)
    {
        Store = store;
        _defaultTtl = defaultTtl;
    }

    public bool Put(byte[] key, byte[] value, byte[]? signature = null, long? timestamp = null, byte[]? publisher = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var previous = Store.GetValueAsync(key).GetAwaiter().GetResult();
        if (previous is not null && previous.Timestamp >= ts) return false;
        return Store.PutValueAsync(key, new StoredValue
        {
            Value = value,
            Signature = signature,
            Timestamp = ts,
            Publisher = publisher is not null ? new PeerId(publisher) : null,
            Ttl = _defaultTtl
        }).GetAwaiter().GetResult();
    }

    public DhtValue? Get(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var value = Store.GetValueAsync(key).GetAwaiter().GetResult();
        return value is null ? null : new DhtValue
        {
            Key = key,
            Value = value.Value,
            Signature = value.Signature,
            Timestamp = value.Timestamp,
            Publisher = value.Publisher?.Bytes.ToArray(),
            StoredAt = value.StoredAt,
            ExpiresAt = value.StoredAt.Add(value.Ttl)
        };
    }

    public bool Contains(byte[] key) => Get(key) is not null;
    public int Count => Store.Count;
    public IEnumerable<byte[]> GetAllKeys() => Store.GetAllKeys()
        .Where(key => Store.HasValueAsync(key).GetAwaiter().GetResult()).Select(key => key.ToArray());
    public bool Remove(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Store.RemoveValueAsync(key).GetAwaiter().GetResult();
    }
    public void Clear()
    {
        foreach (var key in Store.GetAllKeys().ToArray()) Store.RemoveValueAsync(key).GetAwaiter().GetResult();
    }
    public void Dispose() { } // The backing store belongs to its registration owner.
}
