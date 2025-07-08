// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.InternalTable.Crypto;

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    public class FromKeyNodeHashProvider<TKey, TNode>(IKeyOperator<TKey, TNode> keyOperator) : INodeHashProvider<TNode>
    {
        public ValueHash256 GetHash(TNode node) => 
            new ValueHash256(keyOperator.GetNodeHash(node).Bytes);
    }
}

