// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Storage;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Libp2p.Protocols.KadDht.Tests.Integration.WirePeerSafetyTests;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class ConnectionSnapshotTests
{
    [TestCase(Message.Types.MessageType.FindNode, false)]
    [TestCase(Message.Types.MessageType.GetValue, false)]
    [TestCase(Message.Types.MessageType.GetValue, true)]
    [TestCase(Message.Types.MessageType.GetProviders, false)]
    public async Task RegisteredResponsesDecodeEachSessionOnceOutsideCollectionLock(Message.Types.MessageType type, bool hasRecord)
    {
        await using var local = new LocalPeer(new Identity(new byte[32]), null, Substitute.For<IProtocolStackSettings>());
        var identities = new List<CountingPeerProtocol>();
        for (int i = 0; i < 100; i++)
        {
            identities.Add(AddConnectedSession(local, i).Identity);
        }
        var providers = Substitute.For<IProviderStore>();
        providers.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(2000, 20).Select(i => new ProviderRecord { PeerId = Id(i) }).ToArray());
        var values = new InMemoryValueStore();
        if (hasRecord) await values.PutValueAsync(new byte[] { 1 }, new StoredValue { Value = [2], Ttl = TimeSpan.FromHours(1) });
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), providers: providers, values: values,
            nearest: _ => Enumerable.Range(1000, 20).Select(i => Id(i).ToDhtNode(["/ip4/127.0.0.1/tcp/4001"])), local: local);
        var watch = Stopwatch.StartNew();
        var response = await harness.SendAsync(new Message { Type = type, Key = Google.Protobuf.ByteString.CopyFrom(new byte[] { 1 }) });
        watch.Stop();
        int decodes = identities.Sum(identity => identity.Reads);
        TestContext.Out.WriteLine($"{type}: {decodes} session identity decodes, {watch.Elapsed.TotalMilliseconds:F2} ms");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Record is not null, Is.EqualTo(hasRecord));
            Assert.That(response.CloserPeers.Select(p => new PeerId(p.Id.ToByteArray())), Is.EqualTo(Enumerable.Range(1000, 20).Select(Id)));
            Assert.That(response.ProviderPeers.Select(p => new PeerId(p.Id.ToByteArray())),
                Is.EqualTo(type == Message.Types.MessageType.GetProviders ? Enumerable.Range(2000, 20).Select(Id) : []));
            Assert.That(response.CloserPeers.Concat(response.ProviderPeers).All(p => p.Connection == Message.Types.ConnectionType.NotConnected), Is.True);
            Assert.That(identities.Select(identity => identity.Reads), Is.All.EqualTo(1), "Decode each live session identity once per response.");
            Assert.That(identities.Any(identity => identity.ReadUnderLock), Is.False, "Identity conversion must run outside LocalPeer.Sessions' lock.");
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SnapshotRechecksClosureAndKeepsOtherLiveSessionsForSameIdentity(bool anotherSessionLives)
    {
        await using var local = new LocalPeer(new Identity(new byte[32]), null, Substitute.For<IProtocolStackSettings>());
        AddConnectedSession(local, 1);
        var closing = AddConnectedSession(local, 2).Session;
        if (anotherSessionLives) AddConnectedSession(local, 2);
        Task? disconnection = null;
        IEnumerable<DhtNode> Nearest()
        {
            // Serializing the first peer builds the connected-peer snapshot. Close the
            // second peer before its flag is resolved from that same snapshot.
            yield return Id(1).ToDhtNode();
            disconnection = closing.DisconnectAsync();
            yield return Id(2).ToDhtNode();
        }
        var providers = Substitute.For<IProviderStore>();
        providers.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderRecord[] { new() { PeerId = Id(2) } });
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), providers: providers,
            nearest: _ => Nearest(), local: local);
        try
        {
            var response = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));
            Assert.That(disconnection, Is.Not.Null, "The session must close between resolving the two closer-peer flags.");
            await disconnection!;
            var expected = anotherSessionLives ? Message.Types.ConnectionType.Connected : Message.Types.ConnectionType.NotConnected;
            Assert.That(response.CloserPeers.Select(peer => peer.Connection), Is.EqualTo(new[] { Message.Types.ConnectionType.Connected, expected }));
            Assert.That(response.ProviderPeers.Single().Connection, Is.EqualTo(Message.Types.ConnectionType.Connected),
                "Providers are serialized before the closer-peer callback closes this session.");
            // A later response must observe new sessions, rather than retaining a previous snapshot.
            if (!anotherSessionLives) AddConnectedSession(local, 2);
            response = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));
            Assert.That(response.ProviderPeers.Single().Connection, Is.EqualTo(Message.Types.ConnectionType.Connected));
        }
        finally
        {
            if (disconnection is not null) await disconnection;
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EmptyOrInvalidPeerListsDoNotDecodeSessions(bool invalidPeer)
    {
        await using var local = new LocalPeer(new Identity(new byte[32]), null, Substitute.For<IProtocolStackSettings>());
        var identity = AddConnectedSession(local, 1).Identity;
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), local: local,
            nearest: _ => invalidPeer ? [new PeerId([255, 255, 255]).ToDhtNode()] : []);
        var response = await harness.SendAsync(MessageHelper.CreateGetProvidersRequest([1]));
        Assert.That(response.CloserPeers, Is.Empty);
        Assert.That(response.ProviderPeers, Is.Empty);
        Assert.That(identity.Reads, Is.Zero);
    }

    private static (LocalPeer.Session Session, CountingPeerProtocol Identity) AddConnectedSession(LocalPeer local, int index)
    {
        var identity = new CountingPeerProtocol(Id(index), local.Sessions);
        var address = Multiaddress.Decode("/ip4/127.0.0.1/tcp/4001");
        address.Protocols.Add(identity);
        var session = new LocalPeer.Session(local);
        session.State.RemoteAddress = address;
        session.ConnectedTcs.SetResult();
        local.Sessions.Add(session);
        return (session, identity);
    }

    private sealed class CountingPeerProtocol(PeerId peerId, object sessionLock) : P2P(peerId.ToString())
    {
        internal int Reads { get; private set; }
        internal bool ReadUnderLock { get; private set; }

        public override string ToString()
        {
            Reads++;
            ReadUnderLock |= Monitor.IsEntered(sessionLock);
            return base.ToString();
        }
    }
}
