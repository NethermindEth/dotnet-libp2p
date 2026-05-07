// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;

namespace Libp2p.Protocols.KadDht;

internal sealed class MessageSenderAdapter : Kademlia.IKademliaMessageSender<PublicKey, TestNode>
{
    private readonly IKademliaMessageSender<PublicKey, TestNode> _inner;

    public MessageSenderAdapter(IKademliaMessageSender<PublicKey, TestNode> inner)
    {
        _inner = inner;
    }

    public Task Ping(TestNode receiver, CancellationToken token) => _inner.Ping(receiver, token);

    public Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token) =>
        _inner.FindNeighbours(receiver, target, token);
}
