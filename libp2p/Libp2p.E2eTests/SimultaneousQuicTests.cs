// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net.Quic;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Nethermind.Libp2p.E2eTests;

#pragma warning disable CA1416 // QUIC availability is checked before running the test.
#pragma warning disable CA2252

public class SimultaneousQuicTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task LargerPeersEarlierConnectionSurvivesRejectedEagerReplacement(bool incomingStillInitializing)
    {
        if (!QuicListener.IsSupported) Assert.Ignore("QUIC is unavailable.");
        Identity[] identities = [new(), new()];
        Array.Sort(identities, (a, b) => a.PeerId.CompareTo(b.PeerId));
        Schedule schedule = new(identities[0].PeerId);
        using ServiceProvider services = new ServiceCollection().AddLibp2p(b => b.WithQuic())
            .AddSingleton<Libp2pPeerFactory>(sp => new ScheduledFactory(sp.GetRequiredService<IProtocolStackSettings>(),
                sp.GetRequiredService<PeerStore>(), sp.GetRequiredService<IdentifyNotifier>(), schedule)).BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        IProtocolStackSettings settings = services.GetRequiredService<IProtocolStackSettings>();
        ProtocolRef registered = settings.TopProtocols!.Single(p => p.Protocol is QuicProtocol);
        ProtocolRef controlled = new(new ScheduledTransport((QuicProtocol)registered.Protocol, schedule), false);
        // Configure the wrapper before starting peers. All I/O and stream negotiation still
        // run through the DI-created production QUIC, multistream, Identify and Ping stack.
        settings.Protocols![controlled] = settings.Protocols[registered];
        settings.TopProtocols = settings.TopProtocols!.Select(p => ReferenceEquals(p, registered) ? controlled : p).ToArray();
        await using ILocalPeer smaller = factory.Create(identities[0]);
        await using ILocalPeer larger = factory.Create(identities[1]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(12));
        await smaller.StartListenAsync(["/ip4/127.0.0.1/udp/0/quic-v1"]);
        await larger.StartListenAsync(["/ip4/127.0.0.1/udp/0/quic-v1"]);
        Task<ISession> smallDial = smaller.DialAsync(larger.ListenAddresses.ToArray(), deadline.Token);
        Task<ISession> largeDial = larger.DialAsync(smaller.ListenAddresses.ToArray(), deadline.Token);
        INewSessionContext? rejected = null;
        try
        {
            ISession originalAtSmall = await schedule.SmallerIncoming.Task.WaitAsync(deadline.Token);
            ISession originalAtLarge = await schedule.LargerOutgoing.Task.WaitAsync(deadline.Token);
            if (incomingStillInitializing)
                Assert.That(((LocalPeer.Session)originalAtSmall).Connected.IsCompleted, Is.False,
                    "The smaller peer's incoming initializer is held at its gate.");
            else
            {
                schedule.FinishSmallerInitialization.TrySetResult();
                await ((LocalPeer.Session)originalAtSmall).Connected.WaitAsync(deadline.Token);
            }
            Assert.That(largeDial.IsCompleted, Is.False, "The original outbound initializer is held at its gate.");
            schedule.StartSmallerDial.TrySetResult();
            rejected = await schedule.LargerNewIncoming.Task.WaitAsync(deadline.Token);
            TaskCompletionSource rejectedClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration closure = rejected.Token.Register(() => rejectedClosed.TrySetResult());
            await rejectedClosed.Task.WaitAsync(deadline.Token);
            Assert.That(originalAtLarge.Closed.IsCancellationRequested, Is.False,
                "The rejected alternative must leave the original connection alive.");
            Assert.That(originalAtSmall.Closed.IsCancellationRequested, Is.False);
            schedule.FinishSmallerInitialization.TrySetResult();
            schedule.FinishLargerInitialization.TrySetResult();
            ISession[] sessions = await Task.WhenAll(smallDial, largeDial).WaitAsync(deadline.Token);
            Assert.That(sessions[0], Is.SameAs(originalAtSmall));
            Assert.That(sessions[1], Is.SameAs(originalAtLarge));
            await Task.WhenAll(sessions.Select(s => s.DialAsync<PingProtocol>(deadline.Token)));
            await rejected.Completion.WaitAsync(deadline.Token);
            await Task.WhenAll(schedule.SmallerTransportFinished.Task, schedule.LargerReplacementDrained.Task).WaitAsync(deadline.Token);
            await Task.WhenAll(sessions.Select(s => s.DialAsync<PingProtocol>(deadline.Token)));
            Assert.That(sessions.All(s => !s.Closed.IsCancellationRequested), Is.True);
        }
        finally
        {
            schedule.StartSmallerDial.TrySetResult();
            schedule.FinishSmallerInitialization.TrySetResult();
            schedule.FinishLargerInitialization.TrySetResult();
            schedule.LargerNewIncoming.TrySetCanceled();
            deadline.Cancel();
            await ((Task)Task.WhenAll(smallDial, largeDial)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            rejected?.Dispose();
        }
    }

    private sealed class Schedule(PeerId smaller)
    {
        public PeerId Smaller { get; } = smaller;
        public TaskCompletionSource SmallerTransportFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LargerReplacementDrained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartSmallerDial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishSmallerInitialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FinishLargerInitialization { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ISession> SmallerIncoming { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ISession> LargerOutgoing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<INewSessionContext> LargerNewIncoming { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ScheduledFactory : Libp2pPeerFactory
    {
        private readonly IdentifyNotifier _notifier;
        private readonly Schedule _schedule;
        public ScheduledFactory(IProtocolStackSettings settings, PeerStore store, IdentifyNotifier notifier, Schedule schedule)
            : base(settings, store, notifier) { _notifier = notifier; _schedule = schedule; }
        public override ILocalPeer Create(Identity? identity = null) => new ScheduledPeer(protocolStackSettings, PeerStore, identity!, _notifier, _schedule);
    }

    private sealed class ScheduledPeer(IProtocolStackSettings settings, PeerStore store, Identity identity, IdentifyNotifier notifier, Schedule schedule)
        : Libp2pPeer(settings, store, identity, notifier)
    {
        protected override async Task ConnectedTo(ISession session, bool isDialer)
        {
            Task identify = base.ConnectedTo(session, isDialer);
            if (Identity.PeerId == schedule.Smaller && !isDialer)
            {
                schedule.SmallerIncoming.TrySetResult(session);
                await schedule.FinishSmallerInitialization.Task.WaitAsync(session.Closed);
            }
            if (Identity.PeerId != schedule.Smaller && isDialer)
            {
                schedule.LargerOutgoing.TrySetResult(session);
                await schedule.FinishLargerInitialization.Task.WaitAsync(session.Closed);
            }
            await identify;
        }
    }

    private sealed class ScheduledTransport(QuicProtocol inner, Schedule schedule) : ITransportProtocol
    {
        public string Id => inner.Id;
        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => QuicProtocol.GetDefaultAddresses(peerId);
        public static bool IsAddressMatch(Multiaddress address) => QuicProtocol.IsAddressMatch(address);
        public async Task DialAsync(ITransportContext context, Multiaddress address, CancellationToken token)
        {
            if (context.Peer.Identity.PeerId == schedule.Smaller) await schedule.StartSmallerDial.Task.WaitAsync(token);
            try { await inner.DialAsync(new ScheduledContext(context, false, schedule), address, token); }
            finally
            {
                if (context.Peer.Identity.PeerId == schedule.Smaller) schedule.SmallerTransportFinished.TrySetResult();
            }
        }
        public Task ListenAsync(ITransportContext context, Multiaddress address, CancellationToken token)
            => inner.ListenAsync(new ScheduledContext(context, true, schedule), address, token);
    }

    private sealed class ScheduledContext(ITransportContext inner, bool listener, Schedule schedule) : ITransportContext
    {
        public ILocalPeer Peer => inner.Peer;
        public object ConnectionAdmissionScope => inner.ConnectionAdmissionScope;
        public PeerResourceLimits ResourceLimits => inner.ResourceLimits;
        public TimeProvider Clock => inner.Clock;
        public Activity? Activity => inner.Activity;
        public void ListenerReady(Multiaddress address) => inner.ListenerReady(address);
        public void ListenerStopped() => inner.ListenerStopped();
        public INewConnectionContext CreateConnection() => new ScheduledConnection(inner.CreateConnection(), listener, schedule);
    }

    private sealed class ScheduledConnection(INewConnectionContext inner, bool listener, Schedule schedule) : INewConnectionContext
    {
        public ILocalPeer Peer => inner.Peer;
        public string Id => inner.Id;
        public State State => inner.State;
        public CancellationToken Token => inner.Token;
        public Task Completion => inner.Completion;
        public Activity? Activity => inner.Activity;
        public IEnumerable<IProtocol> SubProtocols => inner.SubProtocols;
        public void Dispose()
        {
            try { inner.Dispose(); }
            finally
            {
                if (Peer.Identity.PeerId != schedule.Smaller && listener) schedule.LargerReplacementDrained.TrySetResult();
            }
        }
        public IChannel Upgrade(UpgradeOptions? options = null) => inner.Upgrade(options);
        public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null) => inner.Upgrade(specificProtocol, options);
        public Task Upgrade(IChannel channel, UpgradeOptions? options = null) => inner.Upgrade(channel, options);
        public Task Upgrade(IChannel channel, IProtocol specificProtocol, UpgradeOptions? options = null) => inner.Upgrade(channel, specificProtocol, options);
        public INewSessionContext UpgradeToSession()
        {
            if (Peer.Identity.PeerId == schedule.Smaller && !listener)
            {
                // Hold the smaller peer's registration boundary while the larger peer
                // eagerly admits the new QUIC connection. No timing delay is needed.
                lock (((LocalPeer)Peer).Sessions)
                {
                    if (!schedule.LargerNewIncoming.Task.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Eager registration did not run.");
                    return inner.UpgradeToSession();
                }
            }
            INewSessionContext session = inner.UpgradeToSession();
            if (Peer.Identity.PeerId != schedule.Smaller && listener) schedule.LargerNewIncoming.TrySetResult(session);
            return session;
        }
    }
}
