// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.Kademlia;

public class FromKeyNodeHashProvider<TPublicKey, THash, TNode>(IKeyOperator<TPublicKey, THash, TNode> keyOperator) : INodeHashProvider<THash, TNode> where THash : struct
{
    public THash GetHash(TNode node) => keyOperator.GetNodeHash(node);
}
