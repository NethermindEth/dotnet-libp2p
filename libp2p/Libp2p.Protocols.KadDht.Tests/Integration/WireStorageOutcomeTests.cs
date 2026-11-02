// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Libp2p.Protocols.KadDht.Tests.Integration.WirePeerSafetyTests;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

public class WireStorageOutcomeTests
{
    [TestCase("true")]
    [TestCase("false")]
    [TestCase("exception")]
    [TestCase("unrelated-cancellation")]
    public async Task CustomStoreOutcomeControlsEcho(string outcome)
    {
        var store = Substitute.For<IValueStore>();
        store.PutValueAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<StoredValue>(), Arg.Any<CancellationToken>())
            .Returns(outcome switch
            {
                "exception" => Task.FromException<bool>(new InvalidOperationException("Storage failure")),
                "unrelated-cancellation" => Task.FromException<bool>(new OperationCanceledException()),
                _ => Task.FromResult(outcome == "true")
            });
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), values: store);
        var request = MessageHelper.CreatePutValueRequest([1], [2]);
        var response = await harness.SendAsync(request);
        Assert.That(response.Record, Is.EqualTo(outcome == "true" ? request.Record : null));
        await store.Received(1).PutValueAsync(Arg.Is<ReadOnlyMemory<byte>>(key => key.ToArray().SequenceEqual(new byte[] { 1 })),
            Arg.Is<StoredValue>(value => value.Value.SequenceEqual(new byte[] { 2 })), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MismatchedRecordKeyIsRefusedBeforeStorage()
    {
        var store = Substitute.For<IValueStore>();
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), values: store);
        var request = MessageHelper.CreatePutValueRequest([1], [2]);
        request.Record.Key = ByteString.CopyFrom(new byte[] { 3 });
        Assert.That((await harness.SendAsync(request)).Record, Is.Null);
        await store.DidNotReceive().PutValueAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<StoredValue>(), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DefaultStoreStaleReplacementEchoAgreesWithStoredBytes(bool identical)
    {
        var store = new InMemoryValueStore();
        await store.PutValueAsync(new byte[] { 1 }, new StoredValue
        {
            Value = [2],
            Timestamp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            Ttl = TimeSpan.FromHours(1)
        });
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), values: store);
        var response = await harness.SendAsync(MessageHelper.CreatePutValueRequest([1], identical ? [2] : [3]));
        Assert.That(response.Record is not null, Is.EqualTo(identical));
        Assert.That((await harness.SendAsync(MessageHelper.CreateGetValueRequest([1]))).Record!.Value, Is.EqualTo(ByteString.CopyFrom(new byte[] { 2 })));
    }

    [Test]
    public async Task ClosingChannelCancelsStartedStorageWithoutAcknowledgement()
    {
        var store = Substitute.For<IValueStore>();
        TaskCompletionSource<CancellationToken> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.PutValueAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<StoredValue>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                started.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            });
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), values: store);
        var channel = new Channel();
        var listen = harness.Protocol.ListenAsync(channel.Reverse, harness.Context);
        try
        {
            await ((IChannel)channel).WriteSizeAndProtobufAsync(MessageHelper.CreatePutValueRequest([1], [2]));
            var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(token.CanBeCanceled, Is.True);
            await channel.CloseAsync();
            Assert.That(async () => await listen.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(token.IsCancellationRequested, Is.True);
            Assert.That((await channel.ReadAsync(0, ReadBlockingMode.WaitAny)).Data.Length, Is.Zero);
        }
        finally
        {
            await channel.CloseAsync();
            // Observe completion even if an assertion above fails.
            try { await listen.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HigherLevelPutUsesRealSenderAndDoesNotCountRefusals(bool oneAccepts)
    {
        using var first = new KadWireHarness("direct", new KadDhtOptions { MaxStoredValues = 1 });
        using var second = new KadWireHarness("direct", new KadDhtOptions { MaxStoredValues = 1 });
        await first.SendAsync(MessageHelper.CreatePutValueRequest([9], [9]));
        if (!oneAccepts) await second.SendAsync(MessageHelper.CreatePutValueRequest([9], [9]));
        var local = Substitute.For<ILocalPeer>();
        local.Identity.Returns(new Identity(new byte[32]));
        local.ListenAddresses.Returns([]);
        var firstSession = Substitute.For<ISession>();
        var secondSession = Substitute.For<ISession>();
        local.DialAsync(Id(1), Arg.Any<CancellationToken>()).Returns(firstSession);
        local.DialAsync(Id(2), Arg.Any<CancellationToken>()).Returns(secondSession);
        firstSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(call => first.SendAsync(call.Arg<Message>()));
        secondSession.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(call => second.SendAsync(call.Arg<Message>()));
        var sender = new LibP2pKademliaMessageSender(local);
        var protocol = new KadDhtProtocol(local, sender, sender, new KadDhtOptions { Mode = KadDhtMode.Client },
            new InMemoryValueStore(), new InMemoryProviderStore());
        protocol.AddNode(Id(1).ToDhtNode());
        protocol.AddNode(Id(2).ToDhtNode());
        Assert.That(await protocol.PutValueAsync([1], [2]), Is.EqualTo(oneAccepts));
        await firstSession.Received(1).DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Is<Message>(m => m.Type == Message.Types.MessageType.PutValue), Arg.Any<CancellationToken>());
        await secondSession.Received(1).DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            Arg.Is<Message>(m => m.Type == Message.Types.MessageType.PutValue), Arg.Any<CancellationToken>());
        Assert.That((await first.SendAsync(MessageHelper.CreateGetValueRequest([1]))).Record, Is.Null);
        Assert.That((await second.SendAsync(MessageHelper.CreateGetValueRequest([1]))).Record is not null, Is.EqualTo(oneAccepts));
        // Publication ownership is set only by a successful PUT. Inspect that bookkeeping directly
        // rather than starting a timer-driven maintenance loop in this deterministic test.
        var published = (System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>)typeof(KadDhtProtocol)
            .GetField("_locallyPublishedValues", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(protocol)!;
        Assert.That(published.Keys, Is.EqualTo(oneAccepts ? new[] { Convert.ToBase64String(new byte[] { 1 }) } : Array.Empty<string>()));
    }

    [TestCase("pending", Message.Types.ConnectionType.NotConnected)]
    [TestCase("failed", Message.Types.ConnectionType.NotConnected)]
    [TestCase("connected", Message.Types.ConnectionType.Connected)]
    [TestCase("closing", Message.Types.ConnectionType.NotConnected)]
    [TestCase("absent", Message.Types.ConnectionType.NotConnected)]
    public async Task RegisteredResponsesUseOnlyEstablishedLiveSessions(string state, Message.Types.ConnectionType expected)
    {
        await using var local = new LocalPeer(new Identity(new byte[32]), null, Substitute.For<IProtocolStackSettings>());
        var session = new LocalPeer.Session(local);
        session.State.RemoteAddress = Multiaddress.Decode($"/p2p/{Id(1)}");
        if (state is "connected" or "closing") session.ConnectedTcs.SetResult();
        if (state == "failed") session.ConnectedTcs.SetException(new InvalidOperationException("Failed handshake"));
        if (state == "closing") await session.DisconnectAsync();
        if (state != "absent") local.Sessions.Add(session);
        // An established session for another identity is not evidence for this peer.
        var other = new LocalPeer.Session(local);
        other.State.RemoteAddress = Multiaddress.Decode($"/p2p/{Id(2)}");
        other.ConnectedTcs.SetResult();
        local.Sessions.Add(other);
        var providers = Substitute.For<IProviderStore>();
        providers.GetProvidersAsync(Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ProviderRecord[] { new() { PeerId = Id(1) } });
        using var harness = new KadWireHarness("direct", new KadDhtOptions(), providers: providers,
            nearest: _ => [Id(1).ToDhtNode()], local: local);
        foreach (var request in new[] { MessageHelper.CreateFindNodeRequest([1]), MessageHelper.CreateGetValueRequest([1]), MessageHelper.CreateGetProvidersRequest([1]) })
        {
            var response = await harness.SendAsync(request);
            Assert.That(response.CloserPeers.Single().Connection, Is.EqualTo(expected));
            if (request.Type == Message.Types.MessageType.GetProviders)
                Assert.That(response.ProviderPeers.Single().Connection, Is.EqualTo(expected));
        }
        if (state == "failed") _ = session.Connected.Exception;
    }

    [TestCase(Message.Types.ConnectionType.Connected)]
    [TestCase(Message.Types.ConnectionType.CanConnect)]
    [TestCase(Message.Types.ConnectionType.CannotConnect)]
    public void ExplicitConnectionValuesArePreserved(Message.Types.ConnectionType connection) =>
        Assert.That(MessageHelper.ToWirePeer(Id(1).ToDhtNode(), connection).Connection, Is.EqualTo(connection));
}
