// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Libp2p.Protocols.Gossipsub;

internal class TtlCache<TKey, TItem> : IDisposable where TKey : notnull
{
    private readonly int ttl;

    private struct CachedItem
    {
        public TItem Item { get; set; }
        public DateTimeOffset ValidTill { get; set; }
    }

    private readonly SortedDictionary<TKey, CachedItem> items = new();
    private bool isDisposed;

    public TtlCache(int ttl)
    {
        this.ttl = ttl;
        Task.Run(async () =>
        {
            while (!isDisposed)
            {
                await Task.Delay(5_000);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                TKey[] keys = items.TakeWhile(i => i.Value.ValidTill < now).Select(i => i.Key).ToArray();
                foreach (var keyToRemove in keys)
                {
                    items.Remove(keyToRemove);
                }
            }
        });
    }

    public bool Contains(TKey key) => items.ContainsKey(key);

    public TItem Get(TKey key) => items.GetValueOrDefault(key).Item;

    public void Add(TKey key, TItem item)
    {
        items.TryAdd(key, new CachedItem
        {
            Item = item,
            ValidTill = DateTimeOffset.UtcNow.AddMilliseconds(ttl),
        });
    }

    public void Dispose()
    {
        isDisposed = true;
    }

    internal IList<TItem> ToList() => items.Values.Select(i => i.Item).ToList();
}
