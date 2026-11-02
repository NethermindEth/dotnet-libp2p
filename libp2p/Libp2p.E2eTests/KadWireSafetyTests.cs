// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class KadWireSafetyTests
{
    [Test]
    public async Task NegotiatedMaliciousResponseHasBoundedDiscovery()
    {
        await using var setup = new KadSetup(malicious: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await setup.AddPeersAsync(2).WaitAsync(deadline.Token);
        var sender = new LibP2pKademliaMessageSender(new KadDhtOptions { KSize = 2 }, setup.Peers[0],
            onPeerDiscovered: node => setup.PeerStores[0].Discover(node.Multiaddrs.Select(Multiaddress.Decode).ToArray(), isTrusted: false));
        var receiver = setup.Peers[1].Identity.PeerId.ToDhtNode(setup.Peers[1].ListenAddresses.Select(a => a.ToString()));
        var nodes = await sender.FindNeighbours(receiver, new PublicKey([1]), deadline.Token);
        Assert.That(nodes!.Select(n => n.PeerId), Is.EqualTo(new[] { Peer(0), Peer(1) }));
        Assert.That(setup.PeerStores[0].TryGetPeerInfo(Peer(0), out _), Is.True);
        Assert.That(setup.PeerStores[0].TryGetPeerInfo(Peer(2), out _), Is.False);
        Assert.That(setup.PeerStores[0].TryGetPeerInfo(new PeerId([255, 255, 255]), out _), Is.False);
    }

    [Test]
    public async Task NegotiatedRegisteredHandlerRefusesFullStoreAndPreservesProvidersWithoutAddresses()
    {
        await using var setup = new KadSetup(malicious: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await setup.AddPeersAsync(2).WaitAsync(deadline.Token);
        var receiver = setup.Peers[1].Identity.PeerId.ToDhtNode(setup.Peers[1].ListenAddresses.Select(a => a.ToString()));
        var sender = new LibP2pKademliaMessageSender(setup.Peers[0]);
        Assert.That(await sender.PutValueAsync(receiver, [1], [10], deadline.Token), Is.True);
        Assert.That(await sender.PutValueAsync(receiver, [2], [20], deadline.Token), Is.False);
        Assert.That((await sender.GetValueAsync(receiver, [1], deadline.Token)).Value, Is.EqualTo(new byte[] { 10 }));
        Assert.That((await sender.GetValueAsync(receiver, [2], deadline.Token)).Value, Is.Null);
        var self = setup.Peers[0].Identity.PeerId.ToDhtNode();
        await sender.AddProviderAsync(receiver, [3], self, deadline.Token);
        // Stream dispatch is concurrent. Wait for a subsequent request to observe the stored
        // one-way announcement, with a deadline and a yield between requests.
        GetProvidersResult providers;
        do
        {
            providers = await sender.GetProvidersAsync(receiver, [3], deadline.Token);
            if (providers.Providers.Length > 0) break;
            await Task.Delay(10, deadline.Token);
        } while (true);
        Assert.That(providers.Providers.Single().PeerId, Is.EqualTo(self.PeerId));
        Assert.That(providers.Providers.Single().Multiaddrs, Is.Empty);
    }

    private static PeerId Peer(int index) => new([0x12, 0x20, .. System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index))]);

    private sealed class KadSetup(bool malicious) : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];

        protected override IServiceCollection ConfigureServices(IServiceCollection services)
        {
            services = base.ConfigureServices(services);
            return malicious ? services : services.AddKadDht(o => o.MaxStoredValues = 1);
        }

        protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
        {
            if (!malicious) return builder.WithKadDht();
            return builder.AddRequestResponseProtocol<Message, Message>(KadDhtOptions.DefaultProtocolId, (_, _) =>
            {
                var response = new Message { Type = Message.Types.MessageType.FindNode };
                for (int i = 0; i < 3000; i++)
                {
                    var peer = new Message.Types.Peer { Id = ByteString.CopyFrom(Peer(i).Bytes) };
                    peer.Addrs.Add(ByteString.CopyFrom(Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001").ToBytes()));
                    response.CloserPeers.Add(peer);
                }
                response.CloserPeers.Add(new Message.Types.Peer { Id = ByteString.CopyFrom(new byte[] { 255, 255, 255 }) });
                return Task.FromResult(response);
            }, maxMessageSize: new KadDhtOptions().MaxMessageSize);
        }
    }
}
