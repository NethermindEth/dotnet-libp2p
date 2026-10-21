// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.ObjectModel;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class ConnectionManagementTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task AutoConnect_controls_discovery_replay_and_new_announcements(bool autoConnect)
    {
        PeerStore store = new();
        Multiaddress known = TestPeers.Multiaddr(1);
        Multiaddress discovered = TestPeers.Multiaddr(2);
        store.Discover([known]);
        await using PubsubRouter router = new(store, new PubsubSettings { AutoConnect = autoConnect });
        ILocalPeer peer = CreatePeer();

        await router.StartAsync(peer);
        store.Discover([discovered]);

        Assert.That(store.GetPeerInfo(discovered.GetPeerId()!).Addrs, Has.Member(discovered));
        await peer.Received(autoConnect ? 1 : 0).DialAsync(
            Arg.Is<Multiaddress[]>(a => a.Contains(known)), Arg.Any<CancellationToken>());
        await peer.Received(autoConnect ? 1 : 0).DialAsync(
            Arg.Is<Multiaddress[]>(a => a.Contains(discovered)), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AutoConnect_controls_direct_peer_dials_without_losing_their_addresses(bool autoConnect)
    {
        PeerStore store = new();
        Multiaddress direct = TestPeers.Multiaddr(1);
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            AutoConnect = autoConnect,
            DirectPeers = [direct],
        });
        ILocalPeer peer = CreatePeer();

        await router.StartAsync(peer);

        Assert.That(store.GetPeerInfo(direct.GetPeerId()!).Addrs, Has.Member(direct));
        await peer.Received(autoConnect ? 1 : 0).DialAsync(
            Arg.Is<Multiaddress[]>(a => a.Contains(direct)), Arg.Any<CancellationToken>());
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task AutoConnect_controls_retries_after_either_stream_direction_closes(bool autoConnect, bool inbound)
    {
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings
        {
            AutoConnect = autoConnect,
            ReconnectionPeriod = 60_000,
        });
        ILocalPeer peer = CreatePeer();
        await router.StartAsync(peer);
        Multiaddress address = TestPeers.Multiaddr(1);
        TaskCompletionSource stream = new();
        CancellationToken closed = inbound
            ? router.InboundConnection(address, PubsubRouter.GossipsubProtocolVersionV13, stream.Task, () => Task.CompletedTask).Token
            : router.OutboundConnection(address, PubsubRouter.GossipsubProtocolVersionV13, stream.Task, _ => { });
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = closed.Register(() => removed.TrySetResult());

        stream.SetResult();
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        router.Reconnect(CancellationToken.None);

        await peer.Received(autoConnect ? 1 : 0).DialAsync(
            Arg.Is<Multiaddress[]>(a => a.Contains(address)), Arg.Any<CancellationToken>());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AutoConnect_controls_PX_dials_without_discarding_verified_addresses(bool autoConnect)
    {
        const string topic = "topic";
        PeerStore store = new();
        await using PubsubRouter router = new(store, new PubsubSettings
        {
            AutoConnect = autoConnect,
            AcceptPXThreshold = 0,
            HeartbeatInterval = int.MaxValue,
        });
        ILocalPeer peer = CreatePeer();
        await router.StartAsync(peer);
        router.GetTopic(topic);
        Multiaddress sender = TestPeers.Multiaddr(1);
        Multiaddress exchanged = TestPeers.Multiaddr(2);
        TaskCompletionSource stream = new();
        try
        {
            router.OutboundConnection(sender, PubsubRouter.GossipsubProtocolVersionV13, stream.Task, _ => { });
            router.OnRpc(sender.GetPeerId()!, new Rpc().WithTopics([topic], []));
            await router.Heartbeat();
            Assert.That(((IRoutingStateContainer)router).Mesh[topic], Does.Contain(sender.GetPeerId()));
            ByteString record = Core.Dto.SigningHelper.CreateSignedEnvelope(TestPeers.Identity(2), [exchanged], 1);
            ControlPrune prune = new() { TopicID = topic };
            prune.Peers.Add(new Dto.PeerInfo
            {
                PeerID = ByteString.CopyFrom(exchanged.GetPeerId()!.Bytes),
                SignedPeerRecord = record,
            });

            router.OnRpc(sender.GetPeerId()!, new Rpc
            {
                Control = new ControlMessage
                {
                    Prune = { prune },
                },
            });

            Assert.That(store.GetPeerInfo(exchanged.GetPeerId()!).Addrs, Has.Member(exchanged));
            await peer.Received(autoConnect ? 1 : 0).DialAsync(
                Arg.Is<Multiaddress[]>(a => a.Contains(exchanged)), Arg.Any<CancellationToken>());
        }
        finally
        {
            stream.TrySetResult();
        }
    }

    private static ILocalPeer CreatePeer()
    {
        ILocalPeer peer = Substitute.For<ILocalPeer>();
        peer.Identity.Returns(TestPeers.Identity(0));
        peer.ListenAddresses.Returns(new ObservableCollection<Multiaddress>());
        peer.DialAsync(Arg.Any<Multiaddress[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ISession>(new InvalidOperationException("Connection refused")));
        return peer;
    }
}
