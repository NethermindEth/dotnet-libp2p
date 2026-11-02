// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Kademlia;
using Libp2p.Protocols.KadDht.Integration;
using Multiformats.Address;

namespace Libp2p.Protocols.KadDht;

[Obsolete("Compatibility adapter for the legacy session API.")]
internal sealed class MessageSenderAdapter : Nethermind.Kademlia.IKademliaMessageSender<PublicKey, DhtNode>
{
    private readonly IKademliaMessageSender<PublicKey, TestNode> _inner;

    public MessageSenderAdapter(IKademliaMessageSender<PublicKey, TestNode> inner)
    {
        _inner = inner;
    }

    public async Task<bool> Ping(DhtNode receiver, CancellationToken token)
    {
        await _inner.Ping(ToLegacy(receiver), token);
        return true;
    }

    public async Task<DhtNode[]?> FindNeighbours(DhtNode receiver, PublicKey target, CancellationToken token) =>
        (await _inner.FindNeighbours(ToLegacy(receiver), target, token))?
            .Select(node => node.Id.ToDhtNode(node.Addresses?.Select(a => a.ToString()))).ToArray();

    private static TestNode ToLegacy(DhtNode node) => new(node.PeerId)
    {
        Addresses = node.Multiaddrs.Select(Multiaddress.Decode).ToArray()
    };
}
