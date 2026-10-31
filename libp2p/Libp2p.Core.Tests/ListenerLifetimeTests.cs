// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Nethermind.Libp2p.Core.TestsBase;
using NUnit.Framework;

namespace Nethermind.Libp2p.Core.Tests;

public class ListenerLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposalOwnsStartupAndWaitsForListenerTeardown(bool ready)
    {
        ControlledTransport transport = new();
        LocalPeer peer = CreatePeer(transport);
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1)]);
        await transport.Entered.Task.WaitAsync(Deadline);
        if (ready)
        {
            transport.Context!.ListenerReady(TestPeers.Multiaddr(1));
            await start.WaitAsync(Deadline);
        }
        Task disposal = peer.DisposeAsync().AsTask();
        try
        {
            await transport.Stopping.Task.WaitAsync(Deadline);
            Assert.That(disposal.IsCompleted, Is.False, "Transport teardown is still held at its barrier.");
            Assert.That(peer.DisposeAsync().AsTask(), Is.SameAs(disposal));
            transport.Context!.ListenerReady(TestPeers.Multiaddr(1));
            Assert.That(peer.ListenAddresses, Is.Empty, "A stopped listener must not publish a late address.");
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await peer.StartListenAsync([TestPeers.Multiaddr(1)]));
        }
        finally
        {
            transport.ForceStop.TrySetResult();
            transport.Release.TrySetResult();
            await disposal.WaitAsync(Deadline);
            await start.WaitAsync(Deadline);
        }
        Assert.That(peer.ListenAddresses, Is.Empty);
    }

    [Test]
    public async Task CallerCancellationBeforeReadinessFinishesStartup()
    {
        ControlledTransport transport = new();
        await using LocalPeer peer = CreatePeer(transport);
        using CancellationTokenSource caller = new();
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1)], caller.Token);
        await transport.Entered.Task.WaitAsync(Deadline);
        caller.Cancel();
        await transport.Stopping.Task.WaitAsync(Deadline);
        transport.Release.TrySetResult();
        await start.WaitAsync(Deadline);
        Assert.That(peer.ListenAddresses, Is.Empty);
    }

    [Test]
    public async Task TeardownFailureStillStopsOtherListenersAndSessions()
    {
        ControlledTransport first = new();
        ControlledTransport second = new();
        LocalPeer peer = new MultiListenerPeer(first, second);
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1), TestPeers.Multiaddr(2)]);
        await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(Deadline);
        first.Context!.ListenerReady(TestPeers.Multiaddr(1));
        second.Context!.ListenerReady(TestPeers.Multiaddr(2));
        await start.WaitAsync(Deadline);
        LocalPeer.Session session = new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(3) } };
        peer.UpgradeToSession(session, new ProtocolRef(first), false, null);
        Task disposal = peer.DisposeAsync().AsTask();
        InvalidOperationException expected = new("listener teardown failed");
        try
        {
            await Task.WhenAll(first.Stopping.Task, second.Stopping.Task).WaitAsync(Deadline);
            first.Release.TrySetException(expected);
            Assert.That(disposal.IsCompleted, Is.False, "The other listener still owns its teardown.");
            second.Release.TrySetResult();
            Assert.That(Assert.ThrowsAsync<InvalidOperationException>(async () => await disposal.WaitAsync(Deadline)), Is.SameAs(expected));
            Assert.That(session.Closed.IsCancellationRequested, Is.True);
            Assert.That(peer.ListenAddresses, Is.Empty);
            Assert.That(peer.DisposeAsync().AsTask(), Is.SameAs(disposal));
        }
        finally
        {
            first.ForceStop.TrySetResult();
            second.ForceStop.TrySetResult();
            first.Release.TrySetResult();
            second.Release.TrySetResult();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StartupFailureDoesNotAdvertiseOrPreventOtherListeners(bool completesWithoutReadiness)
    {
        ControlledTransport failed = new() { BindFailure = !completesWithoutReadiness, CompleteWithoutReadiness = completesWithoutReadiness };
        ControlledTransport healthy = new();
        await using LocalPeer peer = new MultiListenerPeer(failed, healthy);
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1), TestPeers.Multiaddr(2)]);
        await healthy.Entered.Task.WaitAsync(Deadline);
        healthy.Context!.ListenerReady(TestPeers.Multiaddr(2));
        try
        {
            await start.WaitAsync(Deadline);
            Assert.That(peer.ListenAddresses, Is.EqualTo(new[] { TestPeers.Multiaddr(2) }));
        }
        finally { healthy.Release.TrySetResult(); }
    }

    [Test]
    public async Task TimedOutStartupCannotAdvertiseLateAndIsStillOwnedByDisposal()
    {
        ControlledTransport transport = new();
        LocalPeer peer = CreatePeer(transport);
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1)]);
        await transport.Entered.Task.WaitAsync(Deadline);
        try
        {
            await start.WaitAsync(TimeSpan.FromSeconds(7));
            await transport.Stopping.Task.WaitAsync(Deadline);
            transport.Context!.ListenerReady(TestPeers.Multiaddr(1));
            Assert.That(peer.ListenAddresses, Is.Empty);
            Task disposal = peer.DisposeAsync().AsTask();
            Assert.That(disposal.IsCompleted, Is.False);
            transport.Release.TrySetResult();
            await disposal.WaitAsync(Deadline);
        }
        finally
        {
            transport.ForceStop.TrySetResult();
            transport.Release.TrySetResult();
            await peer.DisposeAsync();
        }
    }

    [Test]
    public async Task DisposalFromReadinessNotificationWaitsForItsOwner()
    {
        ControlledTransport transport = new();
        LocalPeer peer = CreatePeer(transport);
        Task? disposal = null;
        peer.ListenAddresses.CollectionChanged += (_, _) => disposal ??= peer.DisposeAsync().AsTask();
        // ObservableCollection only rejects reentrant changes when it has multiple subscribers.
        peer.ListenAddresses.CollectionChanged += (_, _) => { };
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1)]);
        await transport.Entered.Task.WaitAsync(Deadline);
        transport.Context!.ListenerReady(TestPeers.Multiaddr(1));
        try
        {
            await transport.Stopping.Task.WaitAsync(Deadline);
            Assert.That(disposal, Is.Not.Null);
            Assert.That(disposal!.IsCompleted, Is.False);
            transport.Release.TrySetResult();
            await disposal.WaitAsync(Deadline);
            await start.WaitAsync(Deadline);
            Assert.That(peer.ListenAddresses, Is.Empty);
        }
        finally
        {
            transport.ForceStop.TrySetResult();
            transport.Release.TrySetResult();
            await peer.DisposeAsync();
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ListenerShutdownPrecedesOwnedServicesAndPreservesAllFailures(bool listenerFails, bool serviceFails)
    {
        ControlledTransport transport = new();
        ProtocolRef reference = new(transport);
        ProtocolStackSettings settings = new() { TopProtocols = [reference], Protocols = new() { [reference] = [] } };
        List<string> releases = [];
        SynchronousResource synchronous = new(() => releases.Add("synchronous"));
        SynchronousResource borrowed = new(() => releases.Add("borrowed"));
        InvalidOperationException listenerFailure = new("listener teardown failed");
        InvalidOperationException serviceFailure = new("service teardown failed");
        AsynchronousResource asynchronous = new(releases, serviceFails ? serviceFailure : null);
        PeerServiceRegistry.Configure(settings, new()
        {
            [typeof(SynchronousResource)] = new(_ => synchronous, true),
            [typeof(IDisposable)] = new(_ => synchronous, true),
            [typeof(IAsyncDisposable)] = new(_ => asynchronous, true),
            [typeof(object)] = new(_ => borrowed, false)
        });
        LocalPeer peer = new(TestPeers.Identity(1), null, settings);
        Assert.That(peer.GetPeerService<SynchronousResource>(), Is.SameAs(synchronous));
        Assert.That(peer.GetPeerService<IDisposable>(), Is.SameAs(synchronous));
        Assert.That(peer.GetPeerService<IAsyncDisposable>(), Is.SameAs(asynchronous));
        Assert.That(peer.GetPeerService<object>(), Is.SameAs(borrowed));
        Task start = peer.StartListenAsync([TestPeers.Multiaddr(1)]);
        await transport.Entered.Task.WaitAsync(Deadline);
        transport.Context!.ListenerReady(TestPeers.Multiaddr(1));
        await start.WaitAsync(Deadline);
        LocalPeer.Session session = new(peer) { State = { RemoteAddress = TestPeers.Multiaddr(2) } };
        peer.UpgradeToSession(session, reference, false, null);
        Task disposal = peer.DisposeAsync().AsTask();
        try
        {
            await transport.Stopping.Task.WaitAsync(Deadline);
            Assert.That(disposal.IsCompleted, Is.False, "Listener teardown is held at its barrier.");
            Assert.That(releases, Is.Empty, "Services must remain alive while the listener is stopping.");
            Assert.That(peer.DisposeAsync().AsTask(), Is.SameAs(disposal));
            if (listenerFails) transport.Release.TrySetException(listenerFailure);
            else transport.Release.TrySetResult();
            await asynchronous.Entered.Task.WaitAsync(Deadline);
            Assert.That(session.Closed.IsCancellationRequested, Is.True);
            Assert.That(peer.ListenAddresses, Is.Empty);
            Assert.That(disposal.IsCompleted, Is.False, "Disposal must await its asynchronous service.");
            Assert.That(releases, Is.EqualTo(new[] { "asynchronous" }));
            asynchronous.Release.TrySetResult();
            List<Exception> expected = [];
            if (listenerFails) expected.Add(listenerFailure);
            if (serviceFails) expected.Add(serviceFailure);
            if (expected.Count == 0) await disposal.WaitAsync(Deadline);
            else
            {
                Assert.ThrowsAsync<InvalidOperationException>(async () => await disposal.WaitAsync(Deadline));
                Assert.That(disposal.Exception!.Flatten().InnerExceptions, Is.EquivalentTo(expected));
            }
            Assert.That(peer.DisposeAsync().AsTask(), Is.SameAs(disposal));
            Assert.That(releases, Is.EqualTo(new[] { "asynchronous", "synchronous" }),
                "Owned aliases are released once in reverse order; borrowed services retain their owner.");
        }
        finally
        {
            transport.ForceStop.TrySetResult();
            transport.Release.TrySetResult();
            asynchronous.Release.TrySetResult();
            try { await disposal.WaitAsync(Deadline); }
            catch when (disposal.IsFaulted) { }
        }
    }

    private sealed class SynchronousResource(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private sealed class AsynchronousResource(List<string> releases, Exception? failure) : IAsyncDisposable
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask DisposeAsync()
        {
            releases.Add("asynchronous");
            Entered.TrySetResult();
            await Release.Task;
            if (failure is not null) throw failure;
        }
    }

    private sealed class MultiListenerPeer(ControlledTransport first, ControlledTransport second)
        : LocalPeer(TestPeers.Identity(1), null, new ProtocolStackSettings())
    {
        private int _selected;
        protected override ProtocolRef SelectProtocol(Multiaddress address) => new(_selected++ == 0 ? first : second);
    }

    private static LocalPeer CreatePeer(ITransportProtocol transport)
    {
        ProtocolRef reference = new(transport);
        return new(TestPeers.Identity(1), null, new ProtocolStackSettings { TopProtocols = [reference], Protocols = new() { [reference] = [] } });
    }

    private sealed class ControlledTransport : ITransportProtocol
    {
        public string Id => "/controlled-listener";
        public bool BindFailure { get; init; }
        public bool CompleteWithoutReadiness { get; init; }
        public TaskCompletionSource ForceStop { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ITransportContext? Context { get; private set; }
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];
        public static bool IsAddressMatch(Multiaddress addr) => true;
        public Task DialAsync(ITransportContext context, Multiaddress addr, CancellationToken token) => throw new NotSupportedException();
        public async Task ListenAsync(ITransportContext context, Multiaddress addr, CancellationToken token)
        {
            Context = context;
            Entered.TrySetResult();
            if (BindFailure) throw new InvalidOperationException("bind failed");
            if (CompleteWithoutReadiness) return;
            try { await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, token), ForceStop.Task); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            Stopping.TrySetResult();
            await Release.Task;
        }
    }
}
