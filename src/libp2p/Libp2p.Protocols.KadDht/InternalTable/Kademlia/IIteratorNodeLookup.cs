// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public interface IIteratorNodeLookup<TNode, TKey> where TNode : notnull
{
    IAsyncEnumerable<TNode> Lookup(TKey target, CancellationToken token);
}

