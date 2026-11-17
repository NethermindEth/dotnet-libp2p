// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Identify.Dto;
using NSubstitute;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Reflection;

namespace Libp2p.E2eTests;

public class IdentifyNotifierTests
{
    [TestCase(0)]
    [TestCase(-1000)]
    [TestCase(1000)]
    public async Task AddressChangesPublishNewerSignedRecordsWhenClockRepeatsOrMoves(int clockChangeMilliseconds)
    {
        Clock clock = new();
        await using TrackedPeer tracked = new(new IdentifyNotifier(clock), 1);
        PeerStore store = new();
        Multiaddress firstAddress = $"/ip4/192.0.2.1/tcp/4001/p2p/{tracked.Peer.Identity.PeerId}";
        Multiaddress secondAddress = $"/ip4/192.0.2.2/tcp/4002/p2p/{tracked.Peer.Identity.PeerId}";

        tracked.Peer.ListenAddresses.Add(firstAddress);
        ulong firstSequence = tracked.TakeSequence();
        Identify first = await tracked.SendPush(firstSequence);
        store.Discover(first.SignedPeerRecord);
        Assert.That(store.GetPeerInfo(tracked.Peer.Identity.PeerId).Addrs, Is.EquivalentTo(new[] { firstAddress }));

        clock.UtcNow += TimeSpan.FromMilliseconds(clockChangeMilliseconds);
        tracked.Peer.ListenAddresses[0] = secondAddress;
        ulong secondSequence = tracked.TakeSequence();
        Identify second = await tracked.SendPush(secondSequence);
        store.Discover(second.SignedPeerRecord);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstSequence, Is.EqualTo((ulong)Clock.InitialTime.ToUnixTimeMilliseconds()));
            Assert.That(secondSequence, Is.EqualTo(Math.Max((ulong)clock.UtcNow.ToUnixTimeMilliseconds(), firstSequence + 1)));
            Assert.That(store.GetPeerInfo(tracked.Peer.Identity.PeerId).Seq, Is.EqualTo(secondSequence));
            Assert.That(store.GetPeerInfo(tracked.Peer.Identity.PeerId).SignedPeerRecord, Is.EqualTo(second.SignedPeerRecord));
            Assert.That(store.GetPeerInfo(tracked.Peer.Identity.PeerId).Addrs, Is.EquivalentTo(new[] { secondAddress }));
        }
    }

    [Test]
    public async Task ConcurrentNotificationsAllocateUniqueSequencesAcrossTrackedPeers()
    {
        Clock clock = new();
        IdentifyNotifier notifier = new(clock);
        TrackedPeer[] peers = Enumerable.Range(1, 8).Select(i => new TrackedPeer(notifier, i)).ToArray();
        try
        {
            await Task.WhenAll(peers.Select(tracked => Task.Run(() =>
            {
                // Each worker owns its collection; only the notifier is shared.
                for (int i = 0; i < 16; i++) tracked.Peer.ListenAddresses.Add(TestPeers.Multiaddr(1));
            })));
            ulong[] sequences = peers.SelectMany(tracked => Enumerable.Range(0, 16).Select(_ => tracked.TakeSequence())).Order().ToArray();
            ulong initial = (ulong)clock.UtcNow.ToUnixTimeMilliseconds();
            Assert.That(sequences, Is.EqualTo(Enumerable.Range(0, 128).Select(i => initial + (ulong)i)));
        }
        finally
        {
            foreach (TrackedPeer peer in peers) await peer.DisposeAsync();
        }
    }

    [Test]
    public async Task DependencyInjectionUsesRegisteredClockAndPreservesDefaultConstruction()
    {
        Clock clock = new();
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
        await using TrackedPeer tracked = new(provider.GetRequiredService<IdentifyNotifier>(), 1);
        tracked.Peer.ListenAddresses.Add(TestPeers.Multiaddr(1));
        Assert.That(tracked.TakeSequence(), Is.EqualTo((ulong)clock.UtcNow.ToUnixTimeMilliseconds()));
        using ServiceProvider defaults = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        Assert.That(defaults.GetRequiredService<IdentifyNotifier>(), Is.Not.Null);
        Assert.That(new IdentifyNotifier(), Is.Not.Null);
    }

    private sealed class Clock : TimeProvider
    {
        public static readonly DateTimeOffset InitialTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow { get; set; } = InitialTime;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TrackedPeer : IAsyncDisposable
    {
        private readonly BlockingCollection<UpgradeOptions> _requests;
        private readonly IdentifyPushProtocol _push;
        public LocalPeer Peer { get; }

        public TrackedPeer(IdentifyNotifier notifier, int identity)
        {
            ProtocolStackSettings stack = new() { Protocols = [] };
            _push = new(stack);
            stack.Protocols.Add(new(_push), []);
            Peer = new(TestPeers.Identity(identity), null, stack);
            LocalPeer.Session session = new(Peer);
            Peer.Sessions.Add(session);
            _requests = (BlockingCollection<UpgradeOptions>)typeof(LocalPeer.Session)
                .GetField("SubDialRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
            notifier.TrackChanges(Peer);
        }

        public ulong TakeSequence()
        {
            Assert.That(_requests.TryTake(out UpgradeOptions? request), Is.True);
            Assert.That(request!.SelectedProtocol, Is.SameAs(_push));
            request.CompletionSource!.TrySetResult(request.Argument);
            return (ulong)request.Argument!;
        }

        public async Task<Identify> SendPush(ulong sequence)
        {
            ISessionContext context = Substitute.For<ISessionContext>();
            context.Peer.Returns(Peer);
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
            Channel channel = new();
            Task<ulong> send = _push.DialAsync(channel, context, sequence);
            Identify message = await channel.Reverse.ReadPrefixedProtobufAsync(Identify.Parser);
            await send;
            return message;
        }

        public ValueTask DisposeAsync() => Peer.DisposeAsync();
    }
}
