// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;

namespace Nethermind.Libp2p.Protocols.Pubsub;

internal class TtlCache<TKey, TItem> : IDisposable where TKey : notnull
{
    private readonly int ttl;

    private struct CachedItem
    {
        public TItem Item { get; set; }

        public CachedItem(TItem item) : this()
        {
            Item = item;
        }

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

    public TItem Get(TKey key, TItem @default) => items.GetValueOrDefault(key, new(@default)).Item;
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

internal class WindowedCache<TKey, TItem> where TKey : notnull
{
    private readonly OrderedDictionary items = new();
    private readonly int topWindows;
    private readonly int[] windows;

    public WindowedCache(int totalWindows, int topWindows)
    {
        this.topWindows = topWindows;
        windows = new int[totalWindows];
    }

    public bool Contains(TKey key) => items.Contains(key);

    public TItem? Get(TKey key) => items.Contains(key) ? (TItem)items[key]! : default;

    public void Put(TKey key, TItem item)
    {
        items.Add(key, item);
    }

    internal IList<TItem> GetTopMessages() => items.Values.Cast<TItem>().Skip(windows.Take(windows.Length - topWindows).Sum()).ToList();

    internal IList<TItem> ToList() => items.Values.Cast<TItem>().ToList();

    internal void Shift()
    {
        int delta = windows[0];
        for (int i = 0; i < delta; i++)
        {
            items.RemoveAt(0);
        }

        for (int i = 1; i < windows.Length; i++)
        {
            windows[i - 1] = windows[i] - delta;
        }
    }
}
