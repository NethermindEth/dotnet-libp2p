// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class DuplicateRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [TestCase("success", true)]
    [TestCase("failure", true)]
    [TestCase("closed", true)]
    [TestCase("cancel", true)]
    [TestCase("success", false)]
    [TestCase("failure", false)]
    [TestCase("closed", false)]
    [TestCase("cancel", false)]
    public async Task DuplicateDialWaitsForTheLiveIncomingSession(string outcome, bool preferIncoming)
    {
        Identity[] identities = [TestPeers.Identity(1), TestPeers.Identity(2)];
        Array.Sort(identities, (a, b) => a.PeerId.CompareTo(b.PeerId));
        DuplicateTransport transport = new();
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using GatedPeer peer = new(identities[preferIncoming ? 1 : 0], transport, initialization.Task);
        Multiaddress address = $"/ip4/127.0.0.1/tcp/4001/p2p/{identities[preferIncoming ? 0 : 1].PeerId}";
        using CancellationTokenSource caller = new();
        Task<ISession> dial = peer.DialAsync(address, caller.Token);
        Task<ISession> waiter = peer.DialAsync(address);
        await transport.Started.Task.WaitAsync(Deadline);
        LocalPeer.Session incoming = new(peer) { State = { RemoteAddress = address } };
        using INewSessionContext context = peer.UpgradeToSession(incoming, peer.Transport, true, null);
        transport.Release.TrySetResult();
        await transport.Rejected.Task.WaitAsync(Deadline);
        Assert.That(dial.IsCompleted, Is.False, "Replacement initialization is held at its barrier.");
        try
        {
            if (outcome == "cancel")
            {
                caller.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(Deadline));
                Assert.That(waiter.IsCompleted, Is.False);
                Assert.That(incoming.Closed.IsCancellationRequested, Is.False, "An outgoing caller does not own this incoming session.");
                initialization.TrySetResult();
                Assert.That(await waiter.WaitAsync(Deadline), Is.SameAs(incoming));
            }
            else if (outcome == "success")
            {
                initialization.TrySetResult();
                Assert.That(await dial.WaitAsync(Deadline), Is.SameAs(incoming));
                Assert.That(await waiter.WaitAsync(Deadline), Is.SameAs(incoming));
                Assert.That(incoming.Connected.IsCompletedSuccessfully, Is.True);
                Assert.That(incoming.Closed.IsCancellationRequested, Is.False);
            }
            else
            {
                if (outcome == "failure") initialization.TrySetException(new InvalidOperationException("replacement failed"));
                else await incoming.DisconnectAsync();
                Assert.ThrowsAsync<SessionExistsException>(async () => await dial.WaitAsync(Deadline));
                Assert.ThrowsAsync<SessionExistsException>(async () => await waiter.WaitAsync(Deadline));
            }
        }
        finally
        {
            initialization.TrySetResult();
            caller.Cancel();
            await peer.DisposeAsync();
            await ((Task)Task.WhenAll(dial, waiter)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task PendingIncomingSessionIsRetainedWhenTheSmallerPeersOutboundSessionArrives()
    {
        Identity[] identities = [TestPeers.Identity(1), TestPeers.Identity(2)];
        Array.Sort(identities, (a, b) => a.PeerId.CompareTo(b.PeerId));
        TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using GatedPeer peer = new(identities[0], new DuplicateTransport(), initialization.Task);
        Multiaddress address = $"/ip4/127.0.0.1/tcp/4001/p2p/{identities[1].PeerId}";
        LocalPeer.Session incoming = new(peer) { State = { RemoteAddress = address } };
        LocalPeer.Session outgoing = new(peer) { State = { RemoteAddress = address } };
        using INewSessionContext accepted = peer.UpgradeToSession(incoming, peer.Transport, true, null);
        try
        {
            Assert.That(incoming.Connected.IsCompleted, Is.False, "The incoming initializer is held at its gate.");
            Assert.Throws<SessionExistsException>(() => peer.UpgradeToSession(outgoing, peer.Transport, false, null));
            Assert.That(peer.Sessions, Is.EqualTo(new[] { incoming }));
            Assert.That(incoming.Closed.IsCancellationRequested, Is.False);
            Assert.That(outgoing.Closed.IsCancellationRequested, Is.True);
            initialization.TrySetResult();
            await incoming.Connected.WaitAsync(Deadline);
            Assert.That(incoming.Closed.IsCancellationRequested, Is.False);
        }
        finally { initialization.TrySetResult(); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReplacementMustInitializeBeforeClosingTheOriginalSession(bool replacementSucceeds)
    {
        Identity[] identities = [TestPeers.Identity(1), TestPeers.Identity(2)];
        Array.Sort(identities, (a, b) => a.PeerId.CompareTo(b.PeerId));
        TaskCompletionSource originalInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource replacementInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ReplacementPeer peer = new(identities[1], originalInitialization.Task, replacementInitialization.Task);
        Multiaddress address = $"/ip4/127.0.0.1/tcp/4001/p2p/{identities[0].PeerId}";
        LocalPeer.Session original = new(peer) { State = { RemoteAddress = address } };
        LocalPeer.Session replacement = new(peer) { State = { RemoteAddress = address } };
        TaskCompletionSource originalClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration closure = original.Closed.Register(() => originalClosed.TrySetResult());
        using INewSessionContext first = peer.UpgradeToSession(original, peer.Transport, false, null);
        using INewSessionContext second = peer.UpgradeToSession(replacement, peer.Transport, true, null);
        try
        {
            Assert.That(original.Closed.IsCancellationRequested, Is.False,
                "The remote peer may have rejected the new connection and retained this one.");
            if (replacementSucceeds)
            {
                replacementInitialization.TrySetResult();
                await replacement.Connected.WaitAsync(Deadline);
                await originalClosed.Task.WaitAsync(Deadline);
                Assert.That(original.Closed.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Does.Contain(replacement));
            }
            else
            {
                replacementInitialization.TrySetException(new InvalidOperationException("Remote rejected replacement"));
                Assert.ThrowsAsync<InvalidOperationException>(async () => await replacement.Connected.WaitAsync(Deadline));
                Assert.That(original.Closed.IsCancellationRequested, Is.False);
                originalInitialization.TrySetResult();
                await original.Connected.WaitAsync(Deadline);
                Assert.That(peer.Sessions, Is.EqualTo(new[] { original }));
            }
        }
        finally
        {
            replacementInitialization.TrySetResult();
            originalInitialization.TrySetResult();
        }
    }

    [Test]
    public async Task ClosingOriginalDoesNotAdmitAnotherSessionWhileReplacementInitializes()
    {
        Identity[] identities = [TestPeers.Identity(1), TestPeers.Identity(2)];
        Array.Sort(identities, (a, b) => a.PeerId.CompareTo(b.PeerId));
        TaskCompletionSource originalInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource replacementInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using ReplacementPeer peer = new(identities[1], originalInitialization.Task, replacementInitialization.Task);
        Multiaddress address = $"/ip4/127.0.0.1/tcp/4001/p2p/{identities[0].PeerId}";
        LocalPeer.Session original = new(peer) { State = { RemoteAddress = address } };
        LocalPeer.Session replacement = new(peer) { State = { RemoteAddress = address } };
        LocalPeer.Session third = new(peer) { State = { RemoteAddress = address } };
        using INewSessionContext first = peer.UpgradeToSession(original, peer.Transport, false, null);
        using INewSessionContext second = peer.UpgradeToSession(replacement, peer.Transport, true, null);
        try
        {
            await original.DisconnectAsync();
            Assert.Throws<SessionExistsException>(() => peer.UpgradeToSession(third, peer.Transport, true, null));
            replacementInitialization.TrySetResult();
            await replacement.Connected.WaitAsync(Deadline);
            Assert.That(peer.Sessions, Is.EqualTo(new[] { replacement }));
        }
        finally
        {
            originalInitialization.TrySetResult();
            replacementInitialization.TrySetResult();
        }
    }

    [Test]
    public async Task ClosedOutboundDialCanReuseAnInboundConnectionBeforeItsSessionRegisters()
    {
        LazyInboundTransport transport = new();
        await using LazyInboundPeer peer = new(transport);
        Task<ISession> dial = peer.DialAsync(TestPeers.Multiaddr(2));
        await transport.PendingInbound.Task.WaitAsync(Deadline);
        transport.ReleaseOutbound.TrySetResult();
        await transport.OutboundClosed.Task.WaitAsync(Deadline);
        using INewSessionContext accepted = transport.IncomingConnection!.UpgradeToSession();

        Assert.That(await dial.WaitAsync(Deadline), Is.SameAs(transport.IncomingSession));
        Assert.That(transport.IncomingSession!.Closed.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task PendingInboundConnectionDoesNotBlockAddressFallback()
    {
        Multiaddress[] addresses = Enumerable.Range(1, 17)
            .Select(i => (Multiaddress)$"/ip4/192.0.2.{i}/tcp/4001/p2p/{TestPeers.PeerId(2)}").ToArray();
        InboundFallbackTransport transport = new(addresses[0], addresses[^1]);
        await using FallbackPeer peer = new(transport);
        using CancellationTokenSource caller = new();
        Task<ISession> dial = peer.DialAsync(addresses, caller.Token);
        await transport.PendingInbound.Task.WaitAsync(Deadline);
        transport.ReleaseOutbound.TrySetResult();
        try
        {
            ISession result = await dial.WaitAsync(Deadline);
            Assert.That(result.RemoteAddress, Is.EqualTo(addresses[^1]));
            Assert.That(transport.IncomingSession!.Closed.IsCancellationRequested, Is.False,
                "Cancelling the borrowed wait must not close the inbound connection.");
        }
        finally
        {
            caller.Cancel();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private sealed class FallbackPeer(InboundFallbackTransport transport)
        : LocalPeer(TestPeers.Identity(1), null, new ProtocolStackSettings())
    {
        private int _initializations;
        protected override ProtocolRef SelectProtocol(Multiaddress addr) => new(transport);
        protected override Task ConnectedTo(ISession session, bool isDialer)
            => Interlocked.Increment(ref _initializations) == 1 ? Task.Delay(Timeout.InfiniteTimeSpan, session.Closed) : Task.CompletedTask;
    }

    private sealed class InboundFallbackTransport(Multiaddress first, Multiaddress last) : ITransportProtocol
    {
        public string Id => "/inbound-fallback-transport";
        public TaskCompletionSource PendingInbound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOutbound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LocalPeer.Session? IncomingSession;
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];
        public static bool IsAddressMatch(Multiaddress addr) => true;
        public Task ListenAsync(ITransportContext context, Multiaddress addr, CancellationToken token) => throw new NotSupportedException();
        public async Task DialAsync(ITransportContext context, Multiaddress address, CancellationToken token)
        {
            if (address != first && address != last)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return;
            }
            using INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = address;
            using INewSessionContext session = connection.UpgradeToSession();
            LocalPeer peer = (LocalPeer)context.Peer;
            if (address == last)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, connection.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                return;
            }
            IncomingSession = new(peer) { State = { RemoteAddress = address } };
            peer.CreateConnection(new ProtocolRef(this), IncomingSession, true, null);
            PendingInbound.TrySetResult();
            await ReleaseOutbound.Task.WaitAsync(token);
        }
    }

    private sealed class LazyInboundPeer(LazyInboundTransport transport)
        : LocalPeer(TestPeers.Identity(1), null, new ProtocolStackSettings())
    {
        protected override ProtocolRef SelectProtocol(Multiaddress addr) => new(transport);
        protected override Task ConnectedTo(ISession session, bool isDialer)
            => isDialer ? Task.Delay(Timeout.InfiniteTimeSpan, session.Closed) : Task.CompletedTask;
    }

    private sealed class LazyInboundTransport : ITransportProtocol
    {
        public string Id => "/lazy-inbound-transport";
        public TaskCompletionSource PendingInbound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOutbound { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OutboundClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LocalPeer.Session? IncomingSession;
        public INewConnectionContext? IncomingConnection;
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];
        public static bool IsAddressMatch(Multiaddress addr) => true;
        public Task ListenAsync(ITransportContext context, Multiaddress addr, CancellationToken token) => throw new NotSupportedException();
        public async Task DialAsync(ITransportContext context, Multiaddress address, CancellationToken token)
        {
            using INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = address;
            using INewSessionContext session = connection.UpgradeToSession();
            LocalPeer peer = (LocalPeer)context.Peer;
            IncomingSession = new(peer) { State = { RemoteAddress = address } };
            IncomingConnection = peer.CreateConnection(new ProtocolRef(this), IncomingSession, true, null);
            PendingInbound.TrySetResult();
            await ReleaseOutbound.Task.WaitAsync(token);
            connection.Dispose();
            OutboundClosed.TrySetResult();
        }
    }

    private sealed class ReplacementPeer(Identity identity, Task original, Task replacement)
        : LocalPeer(identity, null, new ProtocolStackSettings(), resourceLimits: new PeerResourceLimits(maxConcurrentInitializations: 1))
    {
        public ProtocolRef Transport { get; } = new(new DuplicateTransport());
        protected override Task ConnectedTo(ISession session, bool isDialer) => isDialer ? original : replacement;
    }

    private sealed class GatedPeer(Identity identity, DuplicateTransport transport, Task initialization)
        : LocalPeer(identity, null, new ProtocolStackSettings())
    {
        public ProtocolRef Transport { get; } = new(transport);
        protected override ProtocolRef SelectProtocol(Multiaddress addr) => Transport;
        protected override Task ConnectedTo(ISession session, bool isDialer) => initialization;
    }

    private sealed class DuplicateTransport : ITransportProtocol
    {
        public string Id => "/duplicate-transport";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];
        public static bool IsAddressMatch(Multiaddress addr) => true;
        public Task ListenAsync(ITransportContext context, Multiaddress addr, CancellationToken token) => throw new NotSupportedException();
        public async Task DialAsync(ITransportContext context, Multiaddress addr, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            using INewConnectionContext connection = context.CreateConnection();
            connection.State.RemoteAddress = addr;
            try { connection.UpgradeToSession(); }
            catch (SessionExistsException)
            {
                // Yamux can consume this exception. The core must retain the rejection.
                Rejected.TrySetResult();
            }
        }
    }
}
