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

[Obsolete("Use DhtNode and the peer-bound KadDhtProtocol or IDhtMessageSender APIs.")]
internal sealed class KademliaMessageSender : IKademliaMessageSender<PublicKey, TestNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly KadDhtOptions _options;
    private readonly ILogger<KademliaMessageSender>? _logger;
    private readonly TestNodeAddressBook _addressBook;

    public KademliaMessageSender(ILocalPeer localPeer, TestNodeAddressBook addressBook, ILoggerFactory? loggerFactory = null)
        : this(new KadDhtOptions(), localPeer, addressBook, loggerFactory)
    {
    }

    public KademliaMessageSender(KadDhtOptions options, ILocalPeer localPeer, TestNodeAddressBook addressBook, ILoggerFactory? loggerFactory = null)
    {
        WirePeerPolicy.Validate(options);
        _options = options;
        _localPeer = localPeer;
        _addressBook = addressBook;
        _logger = loggerFactory?.CreateLogger<KademliaMessageSender>();
    }

    public async Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token)
    {
        ISession session = await _localPeer.DialAsync(GetAddresses(receiver), token);
        var request = MessageHelper.CreateFindNodeRequest(target.Bytes.ToArray());
        var response = await session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(request, token);

        return new WirePeerPolicy(_options).ReadResponse(response, includeProviders: false).Closer
            .Select(node => new TestNode(node.PeerId)
            {
                Addresses = node.Multiaddrs.Select(Multiaddress.Decode).ToArray()
            })
            .ToArray();
    }

    public async Task Ping(TestNode receiver, CancellationToken token)
    {
        // Kad-DHT PING is deprecated and must not be sent; probe with the ping protocol instead.
        // A successful dial alone can return a cached session without touching the wire.
        LivenessProbe.EnsureSupported(_localPeer);
        ISession session = await _localPeer.DialAsync(GetAddresses(receiver), token);
        await LivenessProbe.ProbeAsync(session, LivenessProbe.DefaultTimeout, token);
    }

    private Multiaddress[] GetAddresses(TestNode node)
    {
        if (node.Addresses is { Length: > 0 } embeddedAddresses) return embeddedAddresses;
        Multiaddress[]? addrs = _addressBook.TryGet(node);
        if (addrs is { Length: > 0 }) return addrs;
        throw new InvalidOperationException("No address known for TestNode. Add it to TestNodeAddressBook.");
    }
}
