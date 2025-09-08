// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Libp2p.Protocols.KadDht.Kademlia;

namespace Nethermind.Network.Discovery.Discv4;

internal sealed class LruCache<THash, T> where THash : struct, IKademiliaHash<THash>
{
    private readonly int _capacity;
    private readonly Dictionary<THash, LinkedListNode<(THash Key, T Value)>> _map = new();
    private readonly LinkedList<(THash Key, T Value)> _lru = new();

    public LruCache(int capacity, string _purpose)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal void Delete(THash key)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _map.Remove(key);
        }
    }

    internal void Set(THash key, T value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            node.Value = (key, value);
            _lru.Remove(node);
            _lru.AddFirst(node);
            return;
        }

        var newNode = new LinkedListNode<(THash, T)>((key, value));
        _lru.AddFirst(newNode);
        _map[key] = newNode;

        if (_map.Count > _capacity)
        {
            var last = _lru.Last;
            if (last != null)
            {
                _map.Remove(last.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    internal bool TryGet(THash key, out T? value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            // Promote
            _lru.Remove(node);
            _lru.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default;
        return false;
    }
}
