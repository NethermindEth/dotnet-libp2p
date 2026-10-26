// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using NSubstitute;
using NUnit.Framework;
using System.Collections.ObjectModel;

namespace Libp2p.E2eTests;

public class IdentifyExposureTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task AdvertisementIncludesOnlyDistinctExposedSessionProtocols(bool push)
    {
        ProtocolStackSettings stack = new() { Protocols = [] };
        Add(stack, "/visible/1", true);
        Add(stack, "/hidden/1", false);
        Add(stack, "/visible/1", true);
        Add(stack, "/hidden/1", false);
        Add(stack, "/mixed/1", false);
        Add(stack, "/mixed/1", true);
        Add(stack, "/mixed-reversed/1", true);
        Add(stack, "/mixed-reversed/1", false);
        NamedProtocol shared = new("/shared/1");
        stack.Protocols!.Add(new ProtocolRef(shared, false), []);
        stack.Protocols.Add(new ProtocolRef(shared, true), []);
        stack.Protocols.Add(new ProtocolRef(new NonSessionProtocol("/connection/1")), []);

        Identify message = await SendAsync(stack, push);

        Assert.That(message.Protocols, Is.EqualTo(new[] { "/visible/1", "/mixed/1", "/mixed-reversed/1", "/shared/1" }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AdvertisementWithNoExposedSessionProtocolsHasEmptyProtocolList(bool push)
    {
        ProtocolStackSettings stack = new() { Protocols = [] };
        Add(stack, "/hidden/1", false);
        Add(stack, "/hidden/1", false);

        Assert.That((await SendAsync(stack, push)).Protocols, Is.Empty);

        stack.Protocols.Clear();
        Assert.That((await SendAsync(stack, push)).Protocols, Is.Empty);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BuilderKeepsExposurePerRegistrationOfSameProtocol(bool push)
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        IPeerFactoryBuilder builder = provider.GetRequiredService<IPeerFactoryBuilder>();
        NamedProtocol shared = new("/shared/1");
        builder.AddProtocol(shared, isExposed: false);
        builder.AddProtocol(shared);
        builder.AddProtocol(shared);
        builder.AddProtocol(new NamedProtocol("/hidden/1"), isExposed: false);
        builder.Build();
        IProtocolStackSettings stack = builder.ServiceProvider.GetRequiredService<IProtocolStackSettings>();

        Assert.That(stack.Protocols!.Keys.Where(r => ReferenceEquals(r.Protocol, shared)).Select(r => r.IsExposed),
            Is.EqualTo(new[] { false, true, true }));
        Identify message = await SendAsync(stack, push);
        Assert.That(message.Protocols.Count(id => id == shared.Id), Is.EqualTo(1));
        Assert.That(message.Protocols, Does.Not.Contain("/hidden/1"));
    }

    [Test]
    public async Task ReceivingPushReplacesSupportedProtocolsWithAdvertisedList()
    {
        Identity sender = TestPeers.Identity(92);
        PeerStore receiverStore = new();
        receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols = ["/old/1"];
        ProtocolStackSettings stack = new() { Protocols = [] };
        Add(stack, "/visible/1", true);
        Add(stack, "/hidden/1", false);

        Channel channel = new();
        ISessionContext senderContext = SenderContext(sender);
        Task receive = new IdentifyPushProtocol(stack, peerStore: receiverStore).ListenAsync(channel.Reverse, ReceiverContext(sender));
        await new IdentifyPushProtocol(stack).DialAsync(channel, senderContext, 2);
        await receive;

        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/visible/1" }));
    }

    [Test]
    public async Task PartialPushWithoutProtocolsPreservesPreviouslyKnownProtocols()
    {
        Identity sender = TestPeers.Identity(93);
        PeerStore receiverStore = new();
        receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols = ["/known/1"];
        Channel channel = new();
        Task receive = new IdentifyPushProtocol(new ProtocolStackSettings(), peerStore: receiverStore)
            .ListenAsync(channel, ReceiverContext(sender));

        await channel.Reverse.WriteSizeAndProtobufAsync(new Identify { PublicKey = sender.PublicKey.ToByteString() });
        await receive;

        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/known/1" }));
    }

    [Test]
    public async Task InitialIdentifyWithoutProtocolsClearsPreviouslyKnownProtocols()
    {
        Identity sender = TestPeers.Identity(94);
        PeerStore receiverStore = new();
        receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols = ["/known/1"];
        Channel channel = new();
        Task receive = new IdentifyProtocol(new ProtocolStackSettings(), peerStore: receiverStore)
            .DialAsync(channel, ReceiverContext(sender));

        await channel.Reverse.WriteSizeAndProtobufAsync(new Identify { PublicKey = sender.PublicKey.ToByteString() });
        await receive;

        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.Empty);
    }

    [Test]
    public async Task PartialPushWithoutPublicKeyUpdatesProtocols()
    {
        Identity sender = TestPeers.Identity(95);
        PeerStore receiverStore = new();
        receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols = ["/old/1"];
        Channel channel = new();
        Task receive = new IdentifyPushProtocol(new ProtocolStackSettings(), peerStore: receiverStore)
            .ListenAsync(channel, ReceiverContext(sender));

        await channel.Reverse.WriteSizeAndProtobufAsync(new Identify { Protocols = { "/next/1" } });
        await receive;

        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/next/1" }));
    }

    [Test]
    public async Task PushWithMismatchedPublicKeyIsRejected()
    {
        Identity sender = TestPeers.Identity(96);
        PeerStore receiverStore = new();
        receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols = ["/old/1"];
        Channel channel = new();
        Task receive = new IdentifyPushProtocol(new ProtocolStackSettings(), peerStore: receiverStore)
            .ListenAsync(channel, ReceiverContext(sender));

        await channel.Reverse.WriteSizeAndProtobufAsync(new Identify
        {
            PublicKey = TestPeers.Identity(97).PublicKey.ToByteString(),
            Protocols = { "/next/1" }
        });

        Assert.ThrowsAsync<PeerConnectionException>(async () => await receive);
        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/old/1" }));
    }

    [Test]
    public async Task InitialIdentifyWithoutPublicKeyIsRejected()
    {
        Identity sender = TestPeers.Identity(98);
        PeerStore receiverStore = new();
        Channel channel = new();
        Task receive = new IdentifyProtocol(new ProtocolStackSettings(), peerStore: receiverStore)
            .DialAsync(channel, ReceiverContext(sender));

        await channel.Reverse.WriteSizeAndProtobufAsync(new Identify { Protocols = { "/next/1" } });

        Assert.ThrowsAsync<PeerConnectionException>(async () => await receive);
        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.Null);
    }

    [Test]
    public async Task StrictRecordPolicyAcceptsPartialPushAfterVerifiedIdentify()
    {
        Identity sender = TestPeers.Identity(99);
        PeerStore receiverStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.RequireCorrect };
        ByteString signedRecord = SigningHelper.CreateSignedEnvelope(sender, [], 1);
        Channel initialChannel = new();
        Task initialRead = new IdentifyProtocol(new ProtocolStackSettings(), settings, receiverStore)
            .DialAsync(initialChannel, ReceiverContext(sender));

        await initialChannel.Reverse.WriteSizeAndProtobufAsync(new Identify
        {
            PublicKey = sender.PublicKey.ToByteString(),
            SignedPeerRecord = signedRecord,
            Protocols = { "/old/1" }
        });
        await initialRead;
        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SignedPeerRecord, Is.EqualTo(signedRecord));

        Channel pushChannel = new();
        Task pushRead = new IdentifyPushProtocol(new ProtocolStackSettings(), settings, receiverStore)
            .ListenAsync(pushChannel, ReceiverContext(sender));
        await pushChannel.Reverse.WriteSizeAndProtobufAsync(new Identify { Protocols = { "/next/1" } });
        await pushRead;

        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/next/1" }));

        Channel invalidChannel = new();
        Task invalidRead = new IdentifyPushProtocol(new ProtocolStackSettings(), settings, receiverStore)
            .ListenAsync(invalidChannel, ReceiverContext(sender));
        await invalidChannel.Reverse.WriteSizeAndProtobufAsync(new Identify
        {
            SignedPeerRecord = ByteString.Empty,
            Protocols = { "/invalid/1" }
        });

        Assert.ThrowsAsync<PeerConnectionException>(async () => await invalidRead);
        Assert.That(receiverStore.GetPeerInfo(sender.PeerId).SupportedProtocols, Is.EqualTo(new[] { "/next/1" }));
    }

    [Test]
    public async Task StrictRecordPolicyRejectsPushWithoutSignedOrPriorRecord()
    {
        Identity sender = TestPeers.Identity(100);
        PeerStore receiverStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.RequireCorrect };
        Channel channel = new();
        Task receive = new IdentifyPushProtocol(new ProtocolStackSettings(), settings, receiverStore)
            .ListenAsync(channel, ReceiverContext(sender));

        await channel.Reverse.WriteSizeAndProtobufAsync(new Identify { Protocols = { "/new/1" } });

        Assert.ThrowsAsync<PeerConnectionException>(async () => await receive);
        Assert.That(receiverStore.TryGetPeerInfo(sender.PeerId, out _), Is.False);
    }

    [Test]
    public async Task HiddenProtocolCanStillBeDialedExplicitly()
    {
        await using HiddenProtocolSetup setup = new();
        await setup.AddPeersAsync(2);

        ISession session = await setup.Peers[0].DialAsync([.. setup.Peers[1].ListenAddresses]);
        int response = await session.DialAsync<IncrementNumberTestProtocol, int, int>(41);

        Assert.That(response, Is.EqualTo(42));
        Assert.That(setup.PeerStores[0].GetPeerInfo(setup.Peers[1].Identity.PeerId).SupportedProtocols,
            Does.Not.Contain("/number/"));
    }

    private static void Add(ProtocolStackSettings stack, string id, bool exposed)
    {
        stack.Protocols!.Add(new ProtocolRef(new NamedProtocol(id), exposed), []);
    }

    private static async Task<Identify> SendAsync(IProtocolStackSettings stack, bool push)
    {
        Channel channel = new();
        ISessionContext context = SenderContext(TestPeers.Identity(91));
        Task send = push
            ? new IdentifyPushProtocol(stack).DialAsync(channel, context, 1)
            : new IdentifyProtocol(stack).ListenAsync(channel, context);
        Identify message = await channel.Reverse.ReadPrefixedProtobufAsync(Identify.Parser);
        await send;
        return message;
    }

    private static ISessionContext SenderContext(Identity identity)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Identity.Returns(identity);
        peer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        context.Peer.Returns(peer);
        context.State.Returns(new State { RemoteAddress = "/ip4/127.0.0.1/tcp/4001" });
        return context;
    }

    private static ISessionContext ReceiverContext(Identity sender)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(new State
        {
            RemoteAddress = TestPeers.Multiaddr(sender),
            RemotePublicKey = sender.PublicKey
        });
        return context;
    }

    private sealed class NamedProtocol(string id) : ISessionProtocol
    {
        public string Id => id;
        public Task ListenAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
        public Task DialAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
    }

    private sealed class NonSessionProtocol(string id) : IProtocol
    {
        public string Id => id;
    }

    private sealed class HiddenProtocolSetup : E2eTestSetup
    {
        protected override IPeerFactoryBuilder ConfigureLibp2p(ILibp2pPeerFactoryBuilder builder)
            => builder.AddProtocol<IncrementNumberTestProtocol>(isExposed: false);

        protected override Multiaddress[] GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];
    }
}
