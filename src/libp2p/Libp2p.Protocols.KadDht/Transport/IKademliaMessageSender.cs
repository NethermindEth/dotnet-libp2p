// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht;

public interface IKademliaMessageSender<TTargetKey, TNode>
{
    Task<TNode[]> FindNeighbours(TNode receiver, TTargetKey target, CancellationToken token);
    Task Ping(TNode receiver, CancellationToken token);
}
