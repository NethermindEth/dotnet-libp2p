using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht.InternalTable.Caching
{
    /// <summary>
    /// A Least Recently Used (LRU) cache implementation.
    /// </summary>
    /// <typeparam name="TKey">The type of keys in the cache.</typeparam>
    /// <typeparam name="TValue">The type of values in the cache.</typeparam>
    public class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly Dictionary<TKey, LinkedListNode<LruCacheItem>> _cacheMap;
        private readonly LinkedList<LruCacheItem> _lruList;
        private readonly TimeSpan _defaultExpiry;

        /// <summary>
        /// Gets the number of items currently in the cache.
        /// </summary>
        public int Count => _cacheMap.Count;

        /// <summary>
        /// Gets the maximum capacity of the cache.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Creates a new instance of LruCache.
        /// </summary>
        /// <param name="capacity">The maximum number of items to store in the cache.</param>
        /// <param name="defaultExpiry">The default expiry time for cache items.</param>
        public LruCache(int capacity, TimeSpan defaultExpiry)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive");
            }

            _capacity = capacity;
            _cacheMap = new Dictionary<TKey, LinkedListNode<LruCacheItem>>(capacity);
            _lruList = new LinkedList<LruCacheItem>();
            _defaultExpiry = defaultExpiry;
        }

        /// <summary>
        /// Adds or updates an item in the cache.
        /// </summary>
        /// <param name="key">The key of the item.</param>
        /// <param name="value">The value of the item.</param>
        /// <param name="expiry">Optional custom expiry time for this item.</param>
        public void Set(TKey key, TValue value, TimeSpan? expiry = null)
        {
            // If the key already exists, remove it
            if (_cacheMap.TryGetValue(key, out var existingNode))
            {
                _lruList.Remove(existingNode);
                _cacheMap.Remove(key);
            }

            // If we're at capacity, remove the least recently used item
            if (_cacheMap.Count >= _capacity && _lruList.Count > 0)
            {
                RemoveLeastRecentlyUsed();
            }

            // Create a new cache item and add it to the front of the list
            var expiryTime = DateTime.UtcNow + (expiry ?? _defaultExpiry);
            var cacheItem = new LruCacheItem(key, value, expiryTime);
            var newNode = _lruList.AddFirst(cacheItem);
            _cacheMap.Add(key, newNode);
        }

        /// <summary>
        /// Tries to get an item from the cache.
        /// </summary>
        /// <param name="key">The key of the item to get.</param>
        /// <param name="value">When this method returns, contains the value associated with the specified key, if the key is found; otherwise, the default value for the type of the value parameter.</param>
        /// <returns>true if the cache contains an element with the specified key; otherwise, false.</returns>
        public bool TryGet(TKey key, out TValue value)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                var cacheItem = node.Value;

                // Check if the item has expired
                if (DateTime.UtcNow > cacheItem.ExpiryTime)
                {
                    // Remove expired item
                    _lruList.Remove(node);
                    _cacheMap.Remove(key);
                    value = default!;
                    return false;
                }

                // Move the accessed item to the front of the list
                _lruList.Remove(node);
                _lruList.AddFirst(node);

                value = cacheItem.Value;
                return true;
            }

            value = default!;
            return false;
        }

        /// <summary>
        /// Removes an item from the cache.
        /// </summary>
        /// <param name="key">The key of the item to remove.</param>
        /// <returns>true if the item was removed; otherwise, false.</returns>
        public bool Remove(TKey key)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _lruList.Remove(node);
                _cacheMap.Remove(key);
                return true;
            }

            return false;
        }

        public void Delete(TKey key)
        {
            Remove(key);
        }

        /// <summary>
        /// Clears all items from the cache.
        /// </summary>
        public void Clear()
        {
            _cacheMap.Clear();
            _lruList.Clear();
        }

        /// <summary>
        /// Removes all expired items from the cache.
        /// </summary>
        /// <returns>The number of items removed.</returns>
        public int RemoveExpiredItems()
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cacheMap
                .Where(kvp => kvp.Value.Value.ExpiryTime <= now)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expiredKeys)
            {
                Remove(key);
            }

            return expiredKeys.Count;
        }

        /// <summary>
        /// Gets all keys currently in the cache.
        /// </summary>
        /// <returns>An enumerable of all keys in the cache.</returns>
        public IEnumerable<TKey> GetKeys()
        {
            return _cacheMap.Keys;
        }

        private void RemoveLeastRecentlyUsed()
        {
            var last = _lruList.Last;
            if (last != null)
            {
                _cacheMap.Remove(last.Value.Key);
                _lruList.RemoveLast();
            }
        }

        private class LruCacheItem
        {
            public TKey Key { get; }
            public TValue Value { get; }
            public DateTime ExpiryTime { get; }

            public LruCacheItem(TKey key, TValue value, DateTime expiryTime)
            {
                Key = key;
                Value = value;
                ExpiryTime = expiryTime;
            }
        }
    }
} 
