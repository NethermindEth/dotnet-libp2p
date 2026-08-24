// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class TtlCache<TKey, TItem> : IDisposable where TKey : notnull
{
    private readonly int ttl;
    private readonly int maxEntries;
    private readonly object sync = new();
    private readonly Dictionary<TKey, CachedItem> items = [];
    private readonly LinkedList<TKey> insertionOrder = [];
    private readonly CancellationTokenSource sweeperCancellation = new();
    private readonly Task sweeperTask;
    private int disposed;
    private readonly record struct CachedItem(TItem Item, DateTimeOffset ValidTill, LinkedListNode<TKey> Node);

    public TtlCache(int ttl, int maxEntries = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        this.ttl = ttl;
        this.maxEntries = maxEntries;
        sweeperTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(5_000, sweeperCancellation.Token);
                    RemoveExpired(DateTimeOffset.UtcNow);
                }
            }
            catch (OperationCanceledException) when (sweeperCancellation.IsCancellationRequested)
            {
            }
        });
    }

    public bool Contains(TKey key) => TryGet(key, out _);

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

    internal int EntryOrderCount
    {
        get
        {
            lock (sync)
            {
                return insertionOrder.Count;
            }
        }
    }

    public bool TryGet(TKey key, out TItem item)
    {
        lock (sync)
        {
            if (items.TryGetValue(key, out CachedItem cachedItem))
            {
                if (cachedItem.ValidTill > DateTimeOffset.UtcNow)
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
            DateTimeOffset now = DateTimeOffset.UtcNow;
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        sweeperCancellation.Cancel();
        sweeperTask.GetAwaiter().GetResult();
        sweeperCancellation.Dispose();

        lock (sync)
        {
            items.Clear();
            insertionOrder.Clear();
        }
    }

    internal IList<TItem> ToList()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (sync)
        {
            return items.Values
                .Where(item => item.ValidTill > now)
                .Select(item => item.Item)
                .ToList();
        }
    }
}

internal class TtlCache<TKey>(int ttl, int maxEntries = int.MaxValue) : TtlCache<TKey, bool>(ttl, maxEntries) where TKey : notnull
{
    public void Add(TKey key) => Add(key, true);
}
