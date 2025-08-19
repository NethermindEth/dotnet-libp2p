// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.InternalTable.Crypto;


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public interface IRoutingTable<TNode> where TNode : notnull
{
    BucketAddResult TryAddOrRefresh(in ValueHash256 hash, TNode item, out TNode? toRefresh);
    bool Remove(in ValueHash256 hash);
    TNode[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude = null, bool excludeSelf = false);
    TNode[] GetAllAtDistance(int i);
    IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<TNode> Bucket)> IterateBuckets();
    TNode? GetByHash(ValueHash256 nodeId);
    void LogDebugInfo();
    event EventHandler<TNode>? OnNodeAdded;
    int Size { get; }
}

