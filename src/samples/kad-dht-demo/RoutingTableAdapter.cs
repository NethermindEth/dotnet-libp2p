// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;

namespace KadDhtDemo;

/// <summary>
/// Adapter that converts IRoutingTable&lt;TKey, TSource&gt; to IRoutingTable&lt;TKey, TTarget&gt;
/// Used to bridge TestNode routing table to DhtNode for SharedDhtState
/// </summary>
public class RoutingTableAdapter<TKey, TSource, TTarget> : IRoutingTable<TKey, TTarget>
    where TKey : struct, IComparable<TKey>
    where TSource : notnull
    where TTarget : notnull
{
    private readonly IRoutingTable<TKey, TSource> _innerTable;
    private readonly Func<TSource, TTarget> _converter;

    public RoutingTableAdapter(IRoutingTable<TKey, TSource> innerTable, Func<TSource, TTarget> converter)
    {
        _innerTable = innerTable;
        _converter = converter;
    }

    public int Size => _innerTable.Size;

    public BucketAddResult TryAddOrRefresh(in TKey hash, TTarget item, out TTarget? toRefresh)
    {
        toRefresh = default;
        throw new NotSupportedException("RoutingTableAdapter is read-only");
    }

    public bool Remove(in TKey hash)
    {
        throw new NotSupportedException("RoutingTableAdapter is read-only");
    }

    public TTarget[] GetKNearestNeighbour(TKey hash, TKey? exclude = null, bool excludeSelf = false)
    {
        return _innerTable.GetKNearestNeighbour(hash, exclude, excludeSelf).Select(_converter).ToArray();
    }

    public TTarget[] GetAllAtDistance(int i)
    {
        return _innerTable.GetAllAtDistance(i).Select(_converter).ToArray();
    }

    public IEnumerable<(TKey Prefix, int Distance, KBucket<TKey, TTarget> Bucket)> IterateBuckets()
    {
        throw new NotSupportedException("RoutingTableAdapter does not support bucket iteration");
    }

    public TTarget? GetByHash(TKey nodeId)
    {
        var source = _innerTable.GetByHash(nodeId);
        return source != null ? _converter(source) : default;
    }

    public void LogDebugInfo()
    {
        _innerTable.LogDebugInfo();
    }

    public event EventHandler<TTarget>? OnNodeAdded;
}
