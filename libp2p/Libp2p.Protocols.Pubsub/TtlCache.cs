// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class TtlCache<TKey, TItem> where TKey : notnull
{
    private readonly int ttl;
    private readonly int maxEntries;
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private readonly Dictionary<TKey, CachedItem> items;
    private readonly LinkedList<TKey> insertionOrder = [];
    private readonly record struct CachedItem(TItem Item, DateTimeOffset ValidTill, LinkedListNode<TKey> Node);

    public TtlCache(int ttl, TimeProvider? timeProvider = null)
        : this(ttl, int.MaxValue, timeProvider)
    {
    }

    public TtlCache(int ttl, int maxEntries, TimeProvider? timeProvider = null, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        items = new Dictionary<TKey, CachedItem>(comparer);
        this.ttl = ttl;
        this.maxEntries = maxEntries;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool Contains(TKey key) => TryGet(key, out _);

    /// <summary>The current time of the cache's clock.</summary>
    public DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>
    /// Looks a key up by an alternate form that the cache comparer supports, without creating the key.
    /// The caller passes <see cref="Now"/>, so a batch of lookups reads the clock once.
    /// </summary>
    public bool Contains<TAlternate>(TAlternate key, DateTimeOffset now) where TAlternate : notnull, allows ref struct
    {
        lock (sync)
        {
            if (items.GetAlternateLookup<TAlternate>().TryGetValue(key, out TKey? actualKey, out CachedItem cachedItem))
            {
                if (cachedItem.ValidTill > now)
                {
                    return true;
                }

                Remove(actualKey, cachedItem);
            }
        }

        return false;
    }

    public TItem Get(TKey key) => TryGet(key, out TItem item) ? item : default!;

    internal int Count
    {
        get
        {
            lock (sync)
            {
                return items.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TItem item)
    {
        lock (sync)
        {
            if (items.TryGetValue(key, out CachedItem cachedItem))
            {
                if (cachedItem.ValidTill > timeProvider.GetUtcNow())
                {
                    item = cachedItem.Item;
                    return true;
                }

                Remove(key, cachedItem);
            }
        }

        item = default!;
        return false;
    }

    internal void RemoveExpired(DateTimeOffset now)
    {
        lock (sync)
        {
            RemoveExpiredLocked(now);
        }
    }

    public void Add(TKey key, TItem item)
    {
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (items.TryGetValue(key, out CachedItem cachedItem))
            {
                if (cachedItem.ValidTill > now)
                {
                    return;
                }

                Remove(key, cachedItem);
            }

            while (items.Count >= maxEntries)
            {
                EvictOldest();
            }

            LinkedListNode<TKey> node = insertionOrder.AddLast(key);
            items.Add(key, new CachedItem(item, now.AddMilliseconds(ttl), node));
        }
    }

    private void RemoveExpiredLocked(DateTimeOffset now)
    {
        // Wall-clock adjustments can make expiration order differ from insertion order.
        List<TKey>? expired = null;
        foreach ((TKey key, CachedItem item) in items)
        {
            if (item.ValidTill <= now)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is not null)
        {
            foreach (TKey key in expired)
            {
                Remove(key, items[key]);
            }
        }
    }

    private void EvictOldest()
    {
        LinkedListNode<TKey>? oldest = insertionOrder.First;
        if (oldest is null)
        {
            throw new InvalidOperationException("TTL cache insertion order was unexpectedly empty.");
        }

        insertionOrder.RemoveFirst();
        if (!items.Remove(oldest.Value))
        {
            throw new InvalidOperationException("TTL cache insertion order was out of sync with its entries.");
        }
    }

    private void Remove(TKey key, CachedItem item)
    {
        items.Remove(key);
        insertionOrder.Remove(item.Node);
    }

    public void Clear()
    {
        lock (sync)
        {
            items.Clear();
            insertionOrder.Clear();
        }
    }

    internal IList<TItem> ToList()
    {
        lock (sync)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            return items.Values
                .Where(item => item.ValidTill > now)
                .Select(item => item.Item)
                .ToList();
        }
    }
}

internal class TtlCache<TKey> : TtlCache<TKey, bool> where TKey : notnull
{
    public TtlCache(int ttl, TimeProvider? timeProvider = null) : base(ttl, timeProvider)
    {
    }

    public TtlCache(int ttl, int maxEntries, TimeProvider? timeProvider = null, IEqualityComparer<TKey>? comparer = null)
        : base(ttl, maxEntries, timeProvider, comparer)
    {
    }

    public void Add(TKey key) => Add(key, true);
}
