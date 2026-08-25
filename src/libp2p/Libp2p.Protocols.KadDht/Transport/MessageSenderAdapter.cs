// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Kademlia;

namespace Libp2p.Protocols.KadDht;

internal sealed class MessageSenderAdapter : Nethermind.Kademlia.IKademliaMessageSender<PublicKey, TestNode>
{
    private readonly IKademliaMessageSender<PublicKey, TestNode> _inner;

    public MessageSenderAdapter(IKademliaMessageSender<PublicKey, TestNode> inner)
    {
        _inner = inner;
    }

    public async Task<bool> Ping(TestNode receiver, CancellationToken token)
    {
        await _inner.Ping(receiver, token);
        return true;
    }

    public async Task<TestNode[]?> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token) =>
        await _inner.FindNeighbours(receiver, target, token);
}
