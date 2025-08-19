// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.InternalTable.Crypto;


namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia
{
    /// <summary>
    /// Define operations for <see cref="TKey"/> and <see cref="TNode"/>.
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TNode"></typeparam>
    public interface IKeyOperator<TKey, in TNode>
    {
        TKey GetKey(TNode node);
        ValueHash256 GetKeyHash(TKey key);
        ValueHash256 GetNodeHash(TNode node) => GetKeyHash(GetKey(node));
        TKey CreateRandomKeyAtDistance(ValueHash256 nodePrefix, int depth);
    }
}

