using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using System;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class ValueHash256NodeHashProvider : INodeHashProvider<ValueHash256>
    {
        public ValueHash256 GetHash(ValueHash256 node)
        {
            return node;
        }
    }
}