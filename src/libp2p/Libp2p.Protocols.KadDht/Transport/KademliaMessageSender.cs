// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Transport;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht;

internal sealed class KademliaMessageSender : IKademliaMessageSender<PublicKey, TestNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<KademliaMessageSender>? _logger;
    private readonly TestNodeAddressBook _addressBook;

    public KademliaMessageSender(ILocalPeer localPeer, TestNodeAddressBook addressBook, ILoggerFactory? loggerFactory = null)
    {
        _localPeer = localPeer;
        _addressBook = addressBook;
        _logger = loggerFactory?.CreateLogger<KademliaMessageSender>();
    }

    public async Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token)
    {
        ISession session = await _localPeer.DialAsync(GetAddresses(receiver), token);
        var request = MessageHelper.CreateFindNodeRequest(target.Bytes.ToArray());
        var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, token);

        return response.CloserPeers
            .Select(MessageHelper.FromWirePeer)
            .OfType<DhtNode>()
            .Select(node => new TestNode(node.PeerId)
            {
                Addresses = node.Multiaddrs.Select(Multiaddress.Decode).ToArray()
            })
            .ToArray();
    }

    public async Task Ping(TestNode receiver, CancellationToken token)
    {
        ISession session = await _localPeer.DialAsync(GetAddresses(receiver), token);
        await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(MessageHelper.CreatePingRequest(), token);
    }

    private Multiaddress[] GetAddresses(TestNode node)
    {
        if (node.Addresses is { Length: > 0 } embeddedAddresses) return embeddedAddresses;
        Multiaddress[]? addrs = _addressBook.TryGet(node);
        if (addrs is { Length: > 0 }) return addrs;
        throw new InvalidOperationException("No address known for TestNode. Add it to TestNodeAddressBook.");
    }
}
