// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Nethermind.Libp2p.E2eTests;

public class PeerConcurrencyTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposalUnbindsAllListenersBeforeAnyFurtherConnection(bool ipv6)
    {
        if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
        using ServiceProvider services = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer peer = factory.Create();
        await using ILocalPeer other = factory.Create();
        string address = ipv6 ? "/ip6/::1/tcp/0" : "/ip4/127.0.0.1/tcp/0";
        await peer.StartListenAsync([address, address]);
        await other.StartListenAsync([address]);
        var endpoints = peer.ListenAddresses.Select(a => a.ToEndPoint()).ToArray();
        Assert.That(endpoints, Has.Length.EqualTo(2));

        await peer.DisposeAsync();

        // Binding is deliberately the first operation on these endpoints after disposal.
        foreach (var endpoint in endpoints)
        {
            using Socket rebound = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            rebound.Bind(endpoint);
        }
        Assert.That(peer.ListenAddresses, Is.Empty);
        await using ILocalPeer client = factory.Create();
        ISession session = await client.DialAsync(other.ListenAddresses.ToArray());
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await session.DialAsync<PingProtocol>(deadline.Token);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task SimultaneousRegisteredDialsReturnUsableSessions(bool plaintext, bool gateInitializations)
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IServiceCollection collection = new ServiceCollection().AddLibp2p(b => plaintext ? b.WithPlaintextEnforced() : b);
        if (gateInitializations) collection.AddSingleton<Libp2pPeerFactory>(sp => new GatedFactory(
            sp.GetRequiredService<IProtocolStackSettings>(), sp.GetRequiredService<PeerStore>(), sp.GetRequiredService<IdentifyNotifier>(), release.Task));
        using ServiceProvider services = collection.BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await using ILocalPeer first = factory.Create();
        await using ILocalPeer second = factory.Create();
        await first.StartListenAsync(["/ip4/127.0.0.1/tcp/0"]);
        await second.StartListenAsync(["/ip4/127.0.0.1/tcp/0"]);
        Task<ISession> firstDial = first.DialAsync(second.ListenAddresses.ToArray(), deadline.Token);
        Task<ISession> secondDial = second.DialAsync(first.ListenAddresses.ToArray(), deadline.Token);
        try
        {
            if (gateInitializations)
                await Task.WhenAll(((GatedPeer)first).OutboundEntered.Task, ((GatedPeer)second).OutboundEntered.Task).WaitAsync(deadline.Token);
            // With gates, both outbound sessions are registered before either starts Identify.
            release.TrySetResult();
            ISession[] sessions = await Task.WhenAll(firstDial, secondDial).WaitAsync(deadline.Token);
            Assert.That(sessions[0].RemotePeerId, Is.EqualTo(second.Identity.PeerId));
            Assert.That(sessions[1].RemotePeerId, Is.EqualTo(first.Identity.PeerId));
            await Task.WhenAll(sessions.Select(s => s.DialAsync<PingProtocol>(deadline.Token)));
            Assert.That(sessions.All(s => !s.Closed.IsCancellationRequested), Is.True);
            if (gateInitializations)
            {
                LocalPeer.Session loser = new[] { ((GatedPeer)first).OutboundSession!, ((GatedPeer)second).OutboundSession! }
                    .Single(s => !sessions.Any(connected => ReferenceEquals(connected, s)));
                Assert.That(loser.Closed.IsCancellationRequested, Is.True);
                await loser.DrainUpgradesAsync().WaitAsync(deadline.Token);
            }
            // The retained connection must remain usable after the rejected upgrades finish.
            await Task.WhenAll(sessions.Select(s => s.DialAsync<PingProtocol>(deadline.Token)));
        }
        finally
        {
            release.TrySetResult();
            deadline.Cancel();
            await ((Task)Task.WhenAll(firstDial, secondDial)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task InstanceDialsDoNotChangeSharedRegistrations()
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p(b => b.AddProtocol<EchoHandler>()).BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        IProtocolStackSettings settings = services.GetRequiredService<IProtocolStackSettings>();
        var registrations = settings.Protocols!.ToArray();
        var children = registrations.Select(p => p.Value.ToArray()).ToArray();
        await using ILocalPeer server = factory.Create();
        await using ILocalPeer first = factory.Create();
        await using ILocalPeer second = factory.Create();
        await server.StartListenAsync(["/ip4/127.0.0.1/tcp/0"]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        ISession[] sessions = await Task.WhenAll(first.DialAsync(server.ListenAddresses.ToArray(), deadline.Token),
            second.DialAsync(server.ListenAddresses.ToArray(), deadline.Token));
        var advertised = (await sessions[0].DialAsync<IdentifyProtocol, IdentifyResult>(deadline.Token)).Protocols.ToArray();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstanceEcho[] instances = Enumerable.Range(0, 12).Select(i => new InstanceEcho($"request-{i}", release.Task)).ToArray();
        Task[] calls = instances.Select((p, i) => sessions[i % 2].DialAsync(p, deadline.Token)).ToArray();
        try
        {
            await Task.WhenAll(instances.Select(p => p.Entered.Task)).WaitAsync(deadline.Token);
            await Task.WhenAll(sessions.Select(s => s.DialAsync<PingProtocol>(deadline.Token)));
            Assert.That((await sessions[1].DialAsync<IdentifyProtocol, IdentifyResult>(deadline.Token)).Protocols, Is.EqualTo(advertised));
            release.TrySetResult();
            await Task.WhenAll(calls).WaitAsync(deadline.Token);
            TypedInstance typed = new();
            Assert.That(await ((LocalPeer.Session)sessions[0]).DialAsync(typed, "typed request", deadline.Token), Is.EqualTo("typed request"));
            Assert.That(instances.Select(p => p.Response), Is.EqualTo(instances.Select(p => p.Request)));
            Assert.That(first.GetProtocol<InstanceEcho>(), Is.Null);
            Assert.That(first.GetProtocol<TypedInstance>(), Is.Null);
            Assert.That(settings.Protocols!.Keys, Is.EqualTo(registrations.Select(p => p.Key)));
            for (int i = 0; i < registrations.Length; i++)
            {
                Assert.That(settings.Protocols[registrations[i].Key], Is.SameAs(registrations[i].Value));
                Assert.That(settings.Protocols[registrations[i].Key], Is.EqualTo(children[i]));
            }
            Assert.That((await sessions[0].DialAsync<IdentifyProtocol, IdentifyResult>(deadline.Token)).Protocols, Is.EqualTo(advertised));
        }
        finally
        {
            release.TrySetResult();
            deadline.Cancel();
            await Task.WhenAll(calls).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TemporaryInstanceCancellationAndFailureLeaveTheSessionUsable(bool cancel)
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p(b => b.AddProtocol<EchoHandler>()).BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        var registrations = services.GetRequiredService<IProtocolStackSettings>().Protocols!.ToArray();
        await using ILocalPeer server = factory.Create();
        await using ILocalPeer client = factory.Create();
        await server.StartListenAsync(["/ip4/127.0.0.1/tcp/0"]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource request = new();
        ISession session = await client.DialAsync(server.ListenAddresses.ToArray(), deadline.Token);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InstanceEcho instance = new("unused", release.Task, fail: !cancel);
        Task call = session.DialAsync(instance, request.Token);
        try
        {
            await instance.Entered.Task.WaitAsync(deadline.Token);
            if (cancel)
            {
                request.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await call.WaitAsync(deadline.Token));
            }
            else Assert.ThrowsAsync<InvalidOperationException>(async () => await call.WaitAsync(deadline.Token));
            await instance.Finished.Task.WaitAsync(deadline.Token);
            await session.DialAsync<PingProtocol>(deadline.Token);
            Assert.That(services.GetRequiredService<IProtocolStackSettings>().Protocols, Is.EqualTo(registrations));
        }
        finally
        {
            request.Cancel();
            release.TrySetResult();
            await call.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task InboundHandlerCanAwaitPeerDisposalWithoutReleasingItsAllowanceEarly()
    {
        ShutdownHandler handler = new();
        InboundStreamLimit limit = new(1);
        using ServiceProvider services = new ServiceCollection().AddLibp2p(b => b.AddProtocol(handler, inboundLimit: limit)).BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer server = factory.Create();
        await using ILocalPeer client = factory.Create();
        await server.StartListenAsync(["/ip4/127.0.0.1/tcp/0"]);
        var endpoint = server.ListenAddresses.Single().ToEndPoint();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(8));
        ISession session = await client.DialAsync(server.ListenAddresses.ToArray(), deadline.Token);
        Task call = session.DialAsync<ShutdownHandler>(deadline.Token);
        try
        {
            await handler.DisposalReturned.Task.WaitAsync(deadline.Token);
            await server.DisposeAsync().AsTask().WaitAsync(deadline.Token);
            Assert.That(server.ListenAddresses, Is.Empty);
            using Socket rebound = new(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            rebound.Bind(endpoint);
            Assert.That(limit.InFlight, Is.EqualTo(1), "The handler still owns its inbound allowance while it drains.");
            handler.Release.TrySetResult();
            await handler.Finished.Task.WaitAsync(deadline.Token);
            await ((LocalPeer.Session)handler.Session!).DrainUpgradesAsync().WaitAsync(deadline.Token);
            Assert.That(limit.InFlight, Is.Zero);
        }
        finally
        {
            handler.Release.TrySetResult();
            deadline.Cancel();
            await call.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ShutdownHandler : ISessionProtocol
    {
        public string Id => "/audit/shutdown/1";
        public TaskCompletionSource DisposalReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ISession? Session;
        public Task DialAsync(IChannel channel, ISessionContext context) => channel.WriteLineAsync("shutdown", context.Token).AsTask();
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            try
            {
                Assert.That(await channel.ReadLineAsync(1024, context.Token), Is.EqualTo("shutdown"));
                Session = ((LocalPeer)context.Peer).Sessions.Single();
                await context.Peer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
                DisposalReturned.TrySetResult();
                await Release.Task;
            }
            catch (Exception failure) { DisposalReturned.TrySetException(failure); throw; }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class EchoHandler : ISessionProtocol
    {
        public string Id => "/audit/echo/1";
        public Task DialAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            string request = await channel.ReadLineAsync(1024, context.Token);
            await channel.WriteLineAsync(request, context.Token);
        }
    }

    private sealed class InstanceEcho(string request, Task release, bool fail = false) : ISessionProtocol
    {
        public string Id => "/audit/echo/1";
        public string Request => request;
        public string? Response { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            try
            {
                Assert.That(context.SubProtocols, Is.Empty);
                Entered.TrySetResult();
                if (fail) throw new InvalidOperationException("instance failed");
                await release.WaitAsync(context.UpgradeOptions!.CancellationToken);
                await channel.WriteLineAsync(request, context.Token);
                Response = await channel.ReadLineAsync(1024, context.Token);
            }
            finally { Finished.TrySetResult(); }
        }
        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
    }

    private sealed class TypedInstance : ISessionProtocol<string, string>
    {
        public string Id => "/audit/echo/1";
        public async Task<string> DialAsync(IChannel channel, ISessionContext context, string request)
        {
            Assert.That(context.SubProtocols, Is.Empty);
            await channel.WriteLineAsync(request, context.Token);
            return await channel.ReadLineAsync(1024, context.Token);
        }
        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
    }

    private sealed class GatedFactory : Libp2pPeerFactory
    {
        private readonly IdentifyNotifier _notifier;
        private readonly Task _release;
        public GatedFactory(IProtocolStackSettings settings, PeerStore store, IdentifyNotifier notifier, Task release)
            : base(settings, store, notifier)
        {
            _notifier = notifier;
            _release = release;
        }
        public override ILocalPeer Create(Identity? identity = null) => new GatedPeer(protocolStackSettings, PeerStore, identity ?? new Identity(), _notifier, _release);
    }

    private sealed class GatedPeer(IProtocolStackSettings settings, PeerStore store, Identity identity, IdentifyNotifier notifier, Task release)
        : Libp2pPeer(settings, store, identity, notifier)
    {
        public LocalPeer.Session? OutboundSession { get; private set; }
        public TaskCompletionSource OutboundEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task ConnectedTo(ISession session, bool isDialer)
        {
            if (isDialer)
            {
                OutboundSession = (LocalPeer.Session)session;
                OutboundEntered.TrySetResult();
                await release.WaitAsync(session.Closed);
            }
            await base.ConnectedTo(session, isDialer);
        }
    }
}
