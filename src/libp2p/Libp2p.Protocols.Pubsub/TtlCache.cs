// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class TtlCache<TKey, TItem> : IDisposable where TKey : notnull
{
    private readonly int ttl;
    private readonly object sync = new();
    private readonly Dictionary<TKey, CachedItem> items = [];
    private readonly CancellationTokenSource sweeperCancellation = new();
    private int disposed;

    private readonly record struct CachedItem(TItem Item, DateTimeOffset ValidTill);

    public TtlCache(int ttl)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        this.ttl = ttl;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(Math.Min(5_000, ttl), sweeperCancellation.Token);
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
            if (items.TryGetValue(key, out CachedItem cachedItem) && cachedItem.ValidTill > DateTimeOffset.UtcNow)
            {
                item = cachedItem.Item;
                return true;
            }
        }

        item = default!;
        return false;
    }

    internal void RemoveExpired(DateTimeOffset now)
    {
        lock (sync)
        {
            if (items.Count == 0)
            {
                return;
            }

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
    }

    public void Add(TKey key, TItem item)
    {
        lock (sync)
        {
            items.TryAdd(key, new CachedItem(item, DateTimeOffset.UtcNow.AddMilliseconds(ttl)));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        sweeperCancellation.Cancel();
        sweeperCancellation.Dispose();

        lock (sync)
        {
            items.Clear();
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

internal class TtlCache<TKey>(int ttl) : TtlCache<TKey, bool>(ttl) where TKey : notnull
{
    public void Add(TKey key) => Add(key, true);
}
