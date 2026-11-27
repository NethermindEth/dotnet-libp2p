// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Integration;
using Libp2p.Protocols.KadDht.Kademlia;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.KadDht.Tests.Integration;

[TestFixture]
public class LibP2pKademliaMessageSenderPingTests
{
    private const string Address = "/ip4/127.0.0.1/tcp/4001";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private ILocalPeer _localPeer = null!;
    private ISession _session = null!;
    private DhtNode _node = null!;

    [SetUp]
    public void Setup()
    {
        _localPeer = Substitute.For<ILocalPeer>();
        _localPeer.GetProtocol<PingProtocol>().Returns(new PingProtocol());
        _session = Substitute.For<ISession>();
        _localPeer.DialAsync(Multiaddress.Decode(Address), Arg.Any<CancellationToken>()).Returns(_session);

        var keyBytes = Enumerable.Repeat((byte)7, 32).ToArray();
        _node = new DhtNode
        {
            PublicKey = new PublicKey(keyBytes),
            PeerId = new PeerId(keyBytes),
            Multiaddrs = new[] { Address }
        };
    }

    [TearDown]
    public async Task TearDown() => await _localPeer.DisposeAsync();

    [Test]
    public async Task Ping_OperationDeadlineExpires_DisconnectsSessionAndReportsFailure()
    {
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromMilliseconds(100));

        bool alive = await sender.Ping(_node, CancellationToken.None);

        Assert.That(alive, Is.False);
        await _session.Received(1).DisconnectAsync();
    }

    [Test]
    public async Task Ping_CallerCancels_KeepsSessionAndPropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(call =>
        {
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
        });
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromSeconds(30));

        Assert.That(async () => await sender.Ping(_node, cts.Token), Throws.InstanceOf<OperationCanceledException>());
        await _session.DidNotReceive().DisconnectAsync();
    }

    /// <summary>
    /// The ping protocol keeps one outbound stream per peer, so overlapping checks must share the
    /// probe already running. The loser's refusal would otherwise reach the health tracker as a
    /// failed request and evict a peer that is answering (#250).
    /// </summary>
    [Test]
    public async Task Ping_OverlappingChecks_ShareOneProbeAndItsAnswer()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref probes);
            reached.TrySetResult();
            return release.Task;
        });
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromSeconds(30));

        Task<bool> first = sender.Ping(_node, CancellationToken.None);
        await reached.Task.WaitAsync(TestTimeout);
        Task<bool> second = sender.Ping(_node, CancellationToken.None);
        release.TrySetResult();

        Assert.That(await first.WaitAsync(TestTimeout), Is.True);
        Assert.That(await second.WaitAsync(TestTimeout), Is.True);
        Assert.That(probes, Is.EqualTo(1), "The second check must join the running probe, not open a stream.");
        await _session.DidNotReceive().DisconnectAsync();
    }

    /// <summary>One caller giving up must not end a probe the others are still waiting on.</summary>
    [Test]
    public async Task Ping_CallerCancelsWhileAnotherWaits_LeavesTheSharedProbeRunning()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            reached.TrySetResult();
            return release.Task;
        });
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromSeconds(30));
        using CancellationTokenSource giveUp = new();

        Task<bool> abandoned = sender.Ping(_node, giveUp.Token);
        await reached.Task.WaitAsync(TestTimeout);
        Task<bool> waiting = sender.Ping(_node, CancellationToken.None);
        giveUp.Cancel();

        Assert.That(async () => await abandoned.WaitAsync(TestTimeout), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(waiting.IsCompleted, Is.False, "The shared probe outlives the caller that left.");

        release.TrySetResult();
        Assert.That(await waiting.WaitAsync(TestTimeout), Is.True);
    }

    /// <summary>
    /// Once our own checks are coalesced, a refusal means a ping from elsewhere holds the stream.
    /// That is not evidence about the peer, and both other answers evict it: the health tracker
    /// removes a pending node on a false result as well as on an exception.
    /// </summary>
    [Test]
    public async Task Ping_AnotherPingHoldsTheStream_DoesNotBecomeADeadPeerVerdict()
    {
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new ProtocolBusyException("a ping is already in flight")));
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromSeconds(30));

        bool alive = await sender.Ping(_node, CancellationToken.None).WaitAsync(TestTimeout);

        Assert.That(alive, Is.True);
        await _session.DidNotReceive().DisconnectAsync();
    }

    /// <summary>A finished probe must not keep answering for the next check.</summary>
    [Test]
    public async Task Ping_AfterAProbeFinishes_TheNextCheckProbesAgain()
    {
        int probes = 0;
        _session.DialAsync<PingProtocol>(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            Interlocked.Increment(ref probes);
            return Task.CompletedTask;
        });
        var sender = new LibP2pKademliaMessageSender(_localPeer, operationTimeout: TimeSpan.FromSeconds(30));

        await sender.Ping(_node, CancellationToken.None).WaitAsync(TestTimeout);
        await sender.Ping(_node, CancellationToken.None).WaitAsync(TestTimeout);

        Assert.That(probes, Is.EqualTo(2));
    }
}
