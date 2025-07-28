using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using System;
using System.Collections.Generic;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class ValueHash256RoutingTable : IRoutingTable<ValueHash256, ValueHash256>
    {
        private readonly KBucketTree<ValueHash256> _kBucketTree;

        public ValueHash256RoutingTable(KBucketTree<ValueHash256> kBucketTree)
        {
            _kBucketTree = kBucketTree;
        }

        public event EventHandler<ValueHash256>? OnNodeAdded
        {
            add => _kBucketTree.OnNodeAdded += value;
            remove => _kBucketTree.OnNodeAdded -= value;
        }

        public BucketAddResult TryAddOrRefresh(in ValueHash256 key, ValueHash256 item, out ValueHash256? toRefresh)
        {
            var result = _kBucketTree.TryAddOrRefresh(key, item, out var toRefreshValue);
            toRefresh = toRefreshValue;
            return result;
        }

        public BucketAddResult TryAddOrRefresh(in ValueHash256 key, ValueHash256 item, out ValueHash256 toRefresh)
        {
            throw new NotImplementedException();
        }

        public bool Remove(in ValueHash256 key)
        {
            return _kBucketTree.Remove(key);
        }

        public ValueHash256[] GetKNearestNeighbour(ValueHash256 key, ValueHash256 exclude = default, bool excludeSelf = false)
        {
            throw new NotImplementedException();
        }

        public ValueHash256[] GetKNearestNeighbour(ValueHash256 key, ValueHash256? exclude = default, bool excludeSelf = false)
        {
            return _kBucketTree.GetKNearestNeighbour(key, exclude, excludeSelf);
        }

        public ValueHash256[] GetAllAtDistance(int i)
        {
            return _kBucketTree.GetAllAtDistance(i);
        }

        public IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<ValueHash256> Bucket)> IterateBuckets()
        {
            return _kBucketTree.IterateBuckets();
        }

        ValueHash256 IRoutingTable<ValueHash256, ValueHash256>.GetByKey(ValueHash256 key)
        {
            throw new NotImplementedException();
        }

        public ValueHash256? GetByKey(ValueHash256 key)
        {
            return _kBucketTree.GetByKey(key);
        }

        public void LogDebugInfo()
        {
            _kBucketTree.LogDebugInfo();
        }

        public int Size => _kBucketTree.Size;
    }
}
