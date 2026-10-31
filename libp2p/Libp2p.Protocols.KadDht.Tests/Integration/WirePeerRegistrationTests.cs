// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Libp2p.Protocols.KadDht.Tests.Integration.WirePeerSafetyTests;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

[Obsolete("Exercises the retained legacy sender API.")]
public class WirePeerRegistrationTests
{
    [TestCase(0, 2)]
    [TestCase(-1, 1)]
    public async Task EncodedBudgetIncludesPeerFieldFraming(int delta, int count)
    {
        var response = new Message();
        for (byte i = 1; i <= 2; i++) response.ProviderPeers.Add(new Message.Types.Peer
        {
            Id = ByteString.CopyFrom(new byte[] { 0, 1, i }),
            Connection = Message.Types.ConnectionType.NotConnected
        });
        // Each peer is 7 protobuf bytes plus a one-byte field tag and one-byte length.
        using var fixture = new SenderFixture(new KadDhtOptions { MaxPeerResponseBytes = 18 + delta }, response);
        var peers = (await fixture.Sender.GetProvidersAsync(Id(-1).ToDhtNode(), [1])).Providers;
        Assert.That(peers, Has.Length.EqualTo(count));
        Assert.That(peers.Select(p => p.PeerId.Bytes), Is.EqualTo(Enumerable.Range(1, count).Select(i => new byte[] { 0, 1, (byte)i })));
    }

    [TestCase("services")]
    [TestCase("fallback")]
    [TestCase("integration")]
    [TestCase("direct")]
    public async Task AnnouncementIdentityAndPrefixLimitsReachCustomStore(string path)
    {
        var store = Substitute.For<IProviderStore>();
        using var harness = new KadWireHarness(path, new KadDhtOptions { MaxProvidersPerKey = 2 }, providers: store);
        var message = new Message { Type = Message.Types.MessageType.AddProvider, Key = ByteString.CopyFrom(new byte[] { 1 }) };
        message.ProviderPeers.Add(new Message.Types.Peer { Id = ByteString.CopyFrom(new byte[100_000]) });
        message.ProviderPeers.Add(WirePeer(1));
        message.ProviderPeers.Add(WirePeer(2));
        await harness.SendAsync(message);
        await store.Received(1).AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Is<ProviderRecord>(record => record.PeerId.Equals(Id(1)) && record.Multiaddrs.Count == 0), Arg.Any<CancellationToken>());
        await store.DidNotReceive().AddProviderAsync(Arg.Any<ReadOnlyMemory<byte>>(),
            Arg.Is<ProviderRecord>(record => record.PeerId.Equals(Id(2))), Arg.Any<CancellationToken>());
    }

    [TestCase(nameof(KadDhtOptions.MaxPeerIdBytes))]
    [TestCase(nameof(KadDhtOptions.MaxAddressesPerPeer))]
    [TestCase(nameof(KadDhtOptions.MaxPeerRecordBytes))]
    [TestCase(nameof(KadDhtOptions.MaxPeerResponseBytes))]
    public void RegistrationRejectsZeroOrNegativePeerLimits(string name)
    {
        var options = new KadDhtOptions();
        typeof(KadDhtOptions).GetProperty(name)!.SetValue(options, 0);
        Assert.That(() => new KadWireHarness("direct", options, providers: Substitute.For<IProviderStore>()), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task ContextIdentityOverConfiguredLimitNeverReachesRoutingCallback()
    {
        var seen = new List<DhtNode>();
        var services = new ServiceCollection();
        using var provider = services.BuildServiceProvider();
        var builder = Substitute.For<IPeerFactoryBuilder>();
        builder.ServiceProvider.Returns(provider);
        RequestResponseProtocol<Message, Message>? protocol = null;
        builder.AddProtocol(Arg.Any<RequestResponseProtocol<Message, Message>>(), Arg.Any<bool>(), Arg.Any<InboundStreamLimit>())
            .Returns(call => { protocol = call.Arg<RequestResponseProtocol<Message, Message>>(); return builder; });
        builder.AddKadDhtProtocols(_ => [], onPeerSeen: seen.Add, options: new KadDhtOptions { MaxPeerIdBytes = 33 });
        var context = Substitute.For<ISessionContext>();
        context.State.Returns(new State { RemoteAddress = Multiaddress.Decode($"/p2p/{Id(1)}") });
        var channel = new Channel();
        var listen = protocol!.ListenAsync(channel.Reverse, context);
        try
        {
            await protocol.DialAsync(channel, context, MessageHelper.CreatePingResponse()).WaitAsync(TimeSpan.FromSeconds(5));
            await listen.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(seen, Is.Empty);
        }
        finally { await channel.CloseAsync(); await listen.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GenericSenderBoundsNeighboursAndChecksExactEcho(bool mismatchedValue)
    {
        var local = Substitute.For<ILocalPeer>();
        var session = Substitute.For<ISession>();
        local.DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>()).Returns(session);
        var response = new Message();
        response.CloserPeers.Add(WirePeer(1, "/ip4/1.1.1.1"));
        response.CloserPeers.Add(WirePeer(2));
        response.CloserPeers.Add(WirePeer(3, "/ip4/3.3.3.3"));
        session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(response);
        await using var sender = new global::Libp2p.Protocols.KadDht.Network.LibP2pKademliaMessageSender<PublicKey, DhtNode>(new KadDhtOptions { KSize = 2 }, local);
        var receiver = Id(-1).ToDhtNode(["/ip4/127.0.0.1"]);
        Assert.That((await sender.FindNeighbours(receiver, new PublicKey([1]), default)).Select(n => n.PeerId), Is.EqualTo(new[] { Id(1) }));
        response.Record = MessageHelper.CreatePutValueRequest(mismatchedValue ? [1] : [2], mismatchedValue ? [3] : [2]).Record;
        Assert.That(await sender.PutValue(receiver, [1], [2]), Is.False);
        response.Record = MessageHelper.CreatePutValueRequest([1], [2]).Record;
        Assert.That(await sender.PutValue(receiver, [1], [2]), Is.True);
    }
}
