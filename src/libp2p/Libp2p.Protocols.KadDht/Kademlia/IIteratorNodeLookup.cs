// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.Kademlia;

public interface IIteratorNodeLookup<THash, TNode>
{
    IAsyncEnumerable<TNode> Lookup(THash target, CancellationToken token);
}
