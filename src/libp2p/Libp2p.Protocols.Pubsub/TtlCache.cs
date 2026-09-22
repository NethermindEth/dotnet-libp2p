// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class TtlCache<TKey, TItem> : IDisposable where TKey : notnull
{
    private readonly int ttl;
    private readonly TimeProvider timeProvider;
    private readonly object sync = new();
    private readonly Dictionary<TKey, CachedItem> items = [];
    private readonly CancellationTokenSource sweeperCancellation = new();
    private readonly Task sweeperTask;
    private int disposed;
    private readonly record struct CachedItem(TItem Item, DateTimeOffset ValidTill);

    public TtlCache(int ttl, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        this.ttl = ttl;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        sweeperTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), this.timeProvider, sweeperCancellation.Token);
                    RemoveExpired(this.timeProvider.GetUtcNow());
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
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (items.TryGetValue(key, out CachedItem cachedItem))
            {
                if (cachedItem.ValidTill > now)
                {
                    return;
                }

                items.Remove(key);
            }

            items.Add(key, new CachedItem(item, now.AddMilliseconds(ttl)));
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
                items.Remove(key);
            }
        }
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

internal class TtlCache<TKey>(int ttl, TimeProvider? timeProvider = null) : TtlCache<TKey, bool>(ttl, timeProvider) where TKey : notnull
{
    public void Add(TKey key) => Add(key, true);
}
