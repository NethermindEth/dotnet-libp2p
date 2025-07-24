using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using System;
using System.Collections.Generic;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class ValueHash256RoutingTable : IRoutingTable<ValueHash256>
    {
        private readonly KBucketTree<ValueHash256> _kBucketTree;

        public ValueHash256RoutingTable(KBucketTree<ValueHash256> kBucketTree)
        {
            _kBucketTree = kBucketTree ?? throw new ArgumentNullException(nameof(kBucketTree));
        }

        public event EventHandler<ValueHash256>? OnNodeAdded
        {
            add => _kBucketTree.OnNodeAdded += value;
            remove => _kBucketTree.OnNodeAdded -= value;
        }

        public BucketAddResult TryAddOrRefresh(in ValueHash256 hash, ValueHash256 item, out ValueHash256? toRefresh)
        {
            return _kBucketTree.TryAddOrRefresh(hash, item, out toRefresh);
        }

        public bool Remove(in ValueHash256 hash)
        {
            return _kBucketTree.Remove(hash);
        }

        public ValueHash256[] GetKNearestNeighbour(ValueHash256 hash, ValueHash256? exclude = null, bool excludeSelf = false)
        {
            return _kBucketTree.GetKNearestNeighbour(hash, exclude, excludeSelf);
        }

        public ValueHash256[] GetAllAtDistance(int i)
        {
            return _kBucketTree.GetAllAtDistance(i);
        }

        public IEnumerable<(ValueHash256 Prefix, int Distance, KBucket<ValueHash256> Bucket)> IterateBuckets()
        {
            return _kBucketTree.IterateBuckets();
        }

        public ValueHash256 GetByHash(ValueHash256 nodeId)
        {
            return _kBucketTree.GetByHash(nodeId);
        }

        public void LogDebugInfo()
        {
            _kBucketTree.LogDebugInfo();
        }

        public int Size => _kBucketTree.Size;
    }
}