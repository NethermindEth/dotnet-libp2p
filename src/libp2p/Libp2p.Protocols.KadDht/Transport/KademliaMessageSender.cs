// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
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
        ISession session = await _localPeer.DialAsync(GetFirstAddress(receiver));
        var request = MessageHelper.CreateFindNodeRequest(target.Bytes.ToArray());
        var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request);

        return response.CloserPeers
            .Select(p => new TestNode { Id = new PeerId(p.Id.ToByteArray()) })
            .ToArray();
    }

    public async Task Ping(TestNode receiver, CancellationToken token)
    {
        ISession session = await _localPeer.DialAsync(GetFirstAddress(receiver));
        await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(MessageHelper.CreatePingRequest());
    }

    private Multiaddress GetFirstAddress(TestNode node)
    {
        Multiaddress[]? addrs = _addressBook.TryGet(node);
        if (addrs is { Length: > 0 }) return addrs[0];
        throw new InvalidOperationException("No address known for TestNode. Add it to TestNodeAddressBook.");
    }
}
