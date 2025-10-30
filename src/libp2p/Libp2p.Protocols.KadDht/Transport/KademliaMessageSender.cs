// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using Microsoft.Extensions.Logging;
using Google.Protobuf;
using Multiformats.Address;
using Libp2p.Protocols.KadDht.Transport;
using System.Linq;

namespace Libp2p.Protocols.KadDht;

internal sealed class KademliaMessageSender : IKademliaMessageSender<PublicKey, TestNode>
{
    private readonly ILocalPeer _localPeer;
    private readonly ILogger<KademliaMessageSender>? _logger;
    private readonly TestNodeAddressBook _addressBook;

    private const string ProtoBase = "/ipfs/kad/1.0.0";
    private const string PingProto = ProtoBase + "/ping";
    private const string FindProto = ProtoBase + "/findneighbours";

    public KademliaMessageSender(ILocalPeer localPeer, TestNodeAddressBook addressBook, ILoggerFactory? loggerFactory = null)
    {
        _localPeer = localPeer;
        _addressBook = addressBook;
        _logger = loggerFactory?.CreateLogger<KademliaMessageSender>();
        // NOTE: ILocalPeer currently has no AddProtocol API; protocol registration must happen through builder extensions elsewhere.
    }
    // Protocol registration removed; implement via IPeerFactoryBuilder.AddProtocol in integration layer.

    public async Task<TestNode[]> FindNeighbours(TestNode receiver, PublicKey target, CancellationToken token)
    {
        // Dial remote using RequestResponse
        ISession session = await _localPeer.DialAsync(GetFirstAddress(receiver));
        // Using the generated DTO namespace (capital P2P) until proto regeneration unifies casing
        var req = new FindNeighboursRequest
        {
            Target = new PublicKeyBytes { Value = ByteString.CopyFrom(target.Bytes.ToArray()) }
        };
        var resp = await session.DialAsync<RequestResponseProtocol<FindNeighboursRequest, FindNeighboursResponse>, FindNeighboursRequest, FindNeighboursResponse>(req);

        // Map to TestNode[]
        return resp.Neighbours
            .Select(n => new TestNode { Id = new PeerId(n.PublicKey.ToByteArray()) })
            .ToArray();
    }

    public async Task Ping(TestNode receiver, CancellationToken token)
    {
        ISession session = await _localPeer.DialAsync(GetFirstAddress(receiver));
        var _ = await session.DialAsync<RequestResponseProtocol<PingRequest, PingResponse>, PingRequest, PingResponse>(new PingRequest());
    }

    private Multiaddress GetFirstAddress(TestNode node)
    {
        Multiaddress[]? addrs = _addressBook.TryGet(node);
        if (addrs is { Length: > 0 }) return addrs[0];
        throw new InvalidOperationException("No address known for TestNode. Add it to TestNodeAddressBook.");
    }
}
