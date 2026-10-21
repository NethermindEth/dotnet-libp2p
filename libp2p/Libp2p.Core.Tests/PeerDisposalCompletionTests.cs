// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
internal class PeerDisposalCompletionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Test]
    public async Task CloseCallbackCannotPublishAnotherSessionDuringPeerDisposal()
    {
        // The second initializer must run inline before disposal visits that session.
        TaskCompletionSource initialization = new();
        PeerStore store = new(2);
        await using CompletionPeer peer = new(store, initialization.Task);
        LocalPeer.Session first = Admit(peer, 2);
        LocalPeer.Session second = Admit(peer, 3);
        PeerStore.PeerInfo firstRetained = store.GetPeerInfo(TestPeers.PeerId(2));
        PeerStore.PeerInfo secondRetained = store.GetPeerInfo(TestPeers.PeerId(3));
        List<PeerId> connected = [];
        peer.OnConnected += session => connected.Add(session.RemotePeerId);
        using CancellationTokenRegistration registration = first.Closed.Register(() => initialization.TrySetResult());

        await peer.DisposeAsync().AsTask().WaitAsync(Deadline);

        Exception? firstFailure = await CaptureFailureAsync(first.Connected.WaitAsync(Deadline));
        Exception? secondFailure = await CaptureFailureAsync(second.Connected.WaitAsync(Deadline));
        Assert.Multiple(() =>
        {
            Assert.That(firstFailure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(secondFailure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(first.Connected.IsCanceled, Is.True);
            Assert.That(second.Connected.IsCanceled, Is.True);
            Assert.That(connected, Is.Empty);
            Assert.That(peer.Sessions, Is.Empty);
            Assert.That(firstRetained.Sessions, Is.Zero);
            Assert.That(secondRetained.Sessions, Is.Zero);
        });
    }

    [Test]
    public async Task DisposalFromRemovalCallbackWaitsForTheDisconnectAndSharesItsFailure()
    {
        PeerStore store = new(1);
        CompletionPeer peer = new(store);
        LocalPeer.Session session = Admit(peer, 2);
        PeerStore.PeerInfo retained = store.GetPeerInfo(TestPeers.PeerId(2));
        InvalidOperationException expected = new("Session removal subscriber failed.");
        Task? disposal = null;
        bool disposalFinishedInsideCallback = true;
        NotifyCollectionChangedEventHandler removed = (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Remove) return;
            disposal = peer.DisposeAsync().AsTask();
            disposalFinishedInsideCallback = disposal.IsCompleted;
            throw expected;
        };
        peer.Sessions.CollectionChanged += removed;
        try
        {
            Task disconnect = session.DisconnectAsync();
            Exception? disconnectFailure = await CaptureFailureAsync(disconnect.WaitAsync(Deadline));
            Assert.That(disposal, Is.Not.Null);
            Exception? disposalFailure = await CaptureFailureAsync(disposal!.WaitAsync(Deadline));
            Task repeatedDisposal = peer.DisposeAsync().AsTask();
            Exception? repeatedFailure = await CaptureFailureAsync(repeatedDisposal.WaitAsync(Deadline));

            Assert.Multiple(() =>
            {
                Assert.That(disposalFinishedInsideCallback, Is.False,
                    "Disposal must include the session whose removal callback is still running.");
                Assert.That(disconnectFailure, Is.SameAs(expected));
                Assert.That(disposalFailure, Is.SameAs(expected));
                Assert.That(repeatedDisposal, Is.SameAs(disposal));
                Assert.That(repeatedFailure, Is.SameAs(expected));
                Assert.That(session.Closed.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty);
                Assert.That(retained.Sessions, Is.Zero);
            });
        }
        finally
        {
            peer.Sessions.CollectionChanged -= removed;
            await CaptureFailureAsync(session.DisconnectAsync().WaitAsync(Deadline));
            await CaptureFailureAsync(peer.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposingConnectionContextClosesItsNativeSessionBeforeOrAfterUpgrade(bool upgrade)
    {
        PeerStore store = new(1);
        await using CompletionPeer peer = new(store);
        LocalPeer.Session session = new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(2) } };
        INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, false, null);
        INewSessionContext? sessionContext = null;
        try
        {
            if (upgrade)
            {
                sessionContext = connection.UpgradeToSession();
                await session.Connected.WaitAsync(Deadline);
            }

            connection.Dispose();

            Assert.That(connection.Token.IsCancellationRequested, Is.True,
                "The context owns the native connection until its transport scope ends.");
            await session.DisconnectAsync().WaitAsync(Deadline);
            connection.Dispose();
            Assert.That(peer.Sessions, Is.Empty);
            if (upgrade) Assert.That(store.GetPeerInfo(TestPeers.PeerId(2)).Sessions, Is.Zero);
            else Assert.That(store.TryGetPeerInfo(TestPeers.PeerId(2), out _), Is.False);
        }
        finally
        {
            sessionContext?.Dispose();
            connection.Dispose();
            await session.DisconnectAsync().WaitAsync(Deadline);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RejectedRegistrationCannotTrackAnAlreadyDisconnectedSession(bool upgrade)
    {
        CompletionPeer peer = new(new PeerStore(1));
        LocalPeer.Session session = new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(2) } };
        using INewConnectionContext connection = peer.CreateConnection(peer.TransportRef, session, false, null);
        using CancellationTokenRegistration registration = session.Closed.Register(() => throw new InvalidOperationException("Observed close failure."));
        try
        {
            Exception? disconnectFailure = await CaptureFailureAsync(session.DisconnectAsync().WaitAsync(Deadline));
            Assert.That(disconnectFailure, Is.InstanceOf<AggregateException>());

            Assert.Throws<OperationCanceledException>(() =>
            {
                if (upgrade) connection.UpgradeToSession();
                else peer.CreateConnection(peer.TransportRef, session, false, null);
            });

            Exception? disposalFailure = await CaptureFailureAsync(peer.DisposeAsync().AsTask().WaitAsync(Deadline));
            Assert.That(disposalFailure, Is.Null,
                "A rejected registration must not make peer disposal revisit an already completed disconnect.");
            Assert.That(peer.Sessions, Is.Empty);
        }
        finally
        {
            await CaptureFailureAsync(session.DisconnectAsync().WaitAsync(Deadline));
            await CaptureFailureAsync(peer.DisposeAsync().AsTask().WaitAsync(Deadline));
        }
    }

    private static LocalPeer.Session Admit(CompletionPeer peer, int remote)
    {
        LocalPeer.Session session = new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(remote) } };
        peer.CreateConnection(peer.TransportRef, session, false, null);
        peer.UpgradeToSession(session, peer.TransportRef, false, null);
        return session;
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception exception) { return exception; }
    }

    private sealed class CompletionPeer(PeerStore store, Task? initialization = null)
        : LocalPeer(TestPeers.Identity(1), store, new ProtocolStackSettings())
    {
        public ProtocolRef TransportRef { get; } = new(Substitute.For<IConnectionProtocol>());
        protected override Task ConnectedTo(ISession session, bool isDialer) => initialization ?? Task.CompletedTask;
    }
}
