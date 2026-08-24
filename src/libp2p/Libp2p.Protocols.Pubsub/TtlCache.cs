// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class TtlCache<TKey, TItem> : IDisposable where TKey : notnull
{
    private readonly int ttl;
    private readonly int maxEntries;
    private readonly object sync = new();
    private readonly Dictionary<TKey, CachedItem> items = [];
    private readonly Queue<(TKey Key, long Sequence)> insertionOrder = [];
    private readonly CancellationTokenSource sweeperCancellation = new();
    private readonly Task sweeperTask;
    private int disposed;
    private long sequence;

    private readonly record struct CachedItem(TItem Item, DateTimeOffset ValidTill, long Sequence);

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

                items.Remove(key);
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

                items.Remove(key);
            }

            while (items.Count >= maxEntries)
            {
                EvictOldest();
            }

            long itemSequence = ++sequence;
            items.Add(key, new CachedItem(item, now.AddMilliseconds(ttl), itemSequence));
            insertionOrder.Enqueue((key, itemSequence));
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
                items.Remove(key);
            }
        }
    }

    private void EvictOldest()
    {
        while (insertionOrder.TryDequeue(out (TKey Key, long Sequence) oldest))
        {
            if (items.TryGetValue(oldest.Key, out CachedItem item) && item.Sequence == oldest.Sequence)
            {
                items.Remove(oldest.Key);
                return;
            }
        }

        throw new InvalidOperationException("TTL cache insertion order was unexpectedly empty.");
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
