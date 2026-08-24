// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class GossipsubV13ProtocolTests
{
    [Test]
    public void Protocol_UsesTheV13ProtocolId()
    {
        PubsubRouter router = new(new PeerStore());

        GossipsubProtocolV13 protocol = new(router);

        Assert.That(protocol.Id, Is.EqualTo(PubsubRouter.GossipsubProtocolVersionV13));
    }

    [Test]
    public void ExtensionRegistryMessages_RoundTrip()
    {
        Rpc rpc = new()
        {
            Control = new ControlMessage
            {
                Extensions = new ControlExtensions { PartialMessages = true },
            },
            Partial = new PartialMessagesExtension
            {
                TopicID = "topic",
                GroupID = ByteString.CopyFrom([1, 2]),
                PartialMessage = ByteString.CopyFrom([3]),
                PartsMetadata = ByteString.CopyFrom([4]),
            },
        };
        rpc.Subscriptions.Add(new Rpc.Types.SubOpts
        {
            Subscribe = true,
            Topicid = "topic",
            RequestsPartial = true,
            SupportsSendingPartial = true,
        });

        Rpc decoded = Rpc.Parser.ParseFrom(rpc.ToByteArray());

        Assert.Multiple(() =>
        {
            Assert.That(decoded.Control.Extensions.PartialMessages, Is.True);
            Assert.That(decoded.Partial.TopicID, Is.EqualTo("topic"));
            Assert.That(decoded.Partial.GroupID.ToByteArray(), Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(decoded.Subscriptions.Single().RequestsPartial, Is.True);
            Assert.That(decoded.Subscriptions.Single().SupportsSendingPartial, Is.True);
        });
    }

    [Test]
    public async Task Router_PrefersV13WhenTheRemotePeerAdvertisesIt()
    {
        PeerStore peerStore = new();
        PubsubRouter router = new(peerStore);
        Multiaddress remoteAddress = TestPeers.Multiaddr(1);
        PeerId remotePeerId = remoteAddress.GetPeerId()!;
        peerStore.GetPeerInfo(remotePeerId).SupportedProtocols = [
            PubsubRouter.GossipsubProtocolVersionV12,
            PubsubRouter.GossipsubProtocolVersionV13,
        ];

        TaskCompletionSource selectedProtocol = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ISession session = Substitute.For<ISession>();
        session.RemoteAddress.Returns(remoteAddress);
        session.DialAsync<GossipsubProtocolV13>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            selectedProtocol.TrySetResult();
            return Task.CompletedTask;
        });

        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.Identity.Returns(TestPeers.Identity(2));
        localPeer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        localPeer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>()).Returns(session);

        using CancellationTokenSource cancellation = new();
        await router.StartAsync(localPeer, cancellation.Token);
        peerStore.Discover([remoteAddress]);

        await selectedProtocol.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _ = session.Received(1).DialAsync<GossipsubProtocolV13>(Arg.Any<CancellationToken>());

        cancellation.Cancel();
    }
}
