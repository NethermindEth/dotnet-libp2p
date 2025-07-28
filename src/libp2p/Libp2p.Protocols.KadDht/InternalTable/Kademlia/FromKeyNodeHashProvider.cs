// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class FromKeyNodeHashProvider<TNode>(IKeyOperator<TNode, TNode> keyOperator) : INodeHashProvider<TNode>
    {
        public ValueHash256 GetHash(TNode node)
        {
            var hash = keyOperator.GetNodeHash(node);
            if (hash is byte[] bytes)
                return new ValueHash256(bytes);
            if (hash is IEnumerable<byte> enumerable)
                return new ValueHash256(enumerable.ToArray());
            throw new InvalidOperationException("GetNodeHash must return byte[] or IEnumerable<byte>.");
        }
    }
}
