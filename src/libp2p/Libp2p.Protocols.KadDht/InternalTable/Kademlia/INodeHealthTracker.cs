// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht.InternalTable.Kademlia;

public interface INodeHealthTracker<TNode>
{
    void OnIncomingMessageFrom(TNode sender);
    void OnRequestFailed(TNode node);
}

