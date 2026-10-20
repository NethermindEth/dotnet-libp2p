// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Reflection;

namespace Libp2p.E2eTests;

public class AutomaticIdentifyTests
{
    [Test]
    public async Task ListenerOnlyPushDoesNotHideTheOutboundImplementation()
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        SplitPushBuilder builder = new(provider);
        builder.AddProtocol<IdentifyPushProtocol>();
        IPeerFactory factory = builder.Build();
        await using LocalPeer peer = (LocalPeer)factory.Create(TestPeers.Identity(1));
        LocalPeer.Session session = new(peer);
        peer.Sessions.Add(session);
        var requests = (BlockingCollection<UpgradeOptions>)typeof(LocalPeer.Session)
            .GetField("SubDialRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;

        peer.ListenAddresses.Add(TestPeers.Multiaddr(1));

        Assert.That(requests.TryTake(out UpgradeOptions? request), Is.True);
        Assert.That(request!.SelectedProtocol, Is.SameAs(peer.GetProtocol<IdentifyPushProtocol>()));
        request.CompletionSource!.TrySetResult(request.Argument);
        await session.DisconnectAsync();
    }

    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 0)]
    [TestCase(true, 1)]
    [TestCase(true, 2)]
    public async Task AutomaticDialSelectsInstalledProtocolOrSkipsOmittedProtocol(bool push, int mode)
    {
        using ServiceProvider provider = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        AutomaticBuilder builder = new(provider, mode);
        IPeerFactory factory = builder.Build();
        await using LocalPeer peer = (LocalPeer)factory.Create(TestPeers.Identity(1));
        LocalPeer.Session session = new(peer);
        var requests = (BlockingCollection<UpgradeOptions>)typeof(LocalPeer.Session)
            .GetField("SubDialRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        Task initialization = Task.CompletedTask;
        try
        {
            if (push)
            {
                peer.Sessions.Add(session);
                peer.ListenAddresses.Add(TestPeers.Multiaddr(1));
            }
            else
            {
                initialization = (Task)peer.GetType().GetMethod("ConnectedTo", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(peer, [session, true])!;
            }
            Assert.That(requests.Count, Is.EqualTo(mode == 0 ? 0 : 1));
            if (mode != 0)
            {
                Assert.That(requests.TryTake(out UpgradeOptions? request), Is.True);
                Assert.That(request!.SelectedProtocol, Is.SameAs(push ? builder.SelectedPush : builder.SelectedIdentify));
                request.CompletionSource!.TrySetResult(push ? request.Argument : null);
            }
            await initialization;
        }
        finally
        {
            await session.DisconnectAsync();
            try { await initialization; }
            catch (OperationCanceledException) { }
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public async Task ConnectionAndAddressChangesHonorSelectedIdentifyProtocols(int mode)
    {
        await using AutomaticSetup setup = new(mode);
        await setup.AddPeersAsync(2);
        TaskCompletionSource remoteConnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Peers[1].OnConnected += _ => remoteConnected.TrySetResult();
        ISession session = await setup.Peers[0].DialAsync([.. setup.Peers[1].ListenAddresses]).WaitAsync(TimeSpan.FromSeconds(5));
        await session.DialAsync<PingProtocol>().WaitAsync(TimeSpan.FromSeconds(5));
        await remoteConnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(session.RemoteAddress.GetPeerId(), Is.EqualTo(setup.Peers[1].Identity.PeerId));
        if (mode == 0) return;
        Assert.That(setup.PeerStores[1].GetPeerInfo(setup.Peers[0].Identity.PeerId).Seq, Is.EqualTo(1));

        Multiaddress advertised = $"/ip4/192.0.2.1/tcp/12345/p2p/{setup.Peers[0].Identity.PeerId}";
        TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.PeerStores[1].OnNewPeer += addresses =>
        {
            if (addresses.Any(a => a.ToString() == advertised.ToString())) received.TrySetResult();
        };

        setup.Peers[0].ListenAddresses.Add(advertised);

        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        PeerStore.PeerInfo info = setup.PeerStores[1].GetPeerInfo(setup.Peers[0].Identity.PeerId);
        Assert.That(info.Seq, Is.GreaterThan(1));
    }

    private sealed class AutomaticSetup(int mode) : E2eTestSetup
    {
        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];
        protected override IServiceCollection ConfigureServices(IServiceCollection services)
            => base.ConfigureServices(services).AddSingleton<IPeerFactoryBuilder>(sp => new AutomaticBuilder(sp, mode));
    }

    private sealed class AutomaticBuilder(IServiceProvider provider, int mode) : Libp2pPeerFactoryBuilder(provider)
    {
        public IProtocol? SelectedIdentify { get; private set; }
        public IProtocol? SelectedPush { get; private set; }

        protected override ProtocolRef? Identify()
        {
            SelectedIdentify = mode switch { 1 => Get<ForwardedIdentify>().Protocol, 2 => Get<DerivedIdentify>().Protocol, _ => null };
            return SelectedIdentify is null ? null : new(SelectedIdentify);
        }

        protected override ProtocolRef? IdentifyPush()
        {
            SelectedPush = mode switch { 1 => Get<ForwardedPush>().Protocol, 2 => Get<DerivedPush>().Protocol, _ => null };
            return SelectedPush is null ? null : new(SelectedPush);
        }
    }

    private sealed class DerivedIdentify(IProtocolStackSettings stack, PeerStore store) : IdentifyProtocol(stack, peerStore: store);
    private sealed class DerivedPush(IProtocolStackSettings stack, PeerStore store) : IdentifyPushProtocol(stack, peerStore: store);

    private sealed class ForwardedIdentify(IProtocolStackSettings stack, PeerStore store) : ISessionProtocol
    {
        private readonly IdentifyProtocol _inner = new(stack, peerStore: store);
        public string Id => IdentifyProtocol.ProtocolId;
        public Task DialAsync(IChannel channel, ISessionContext context) => _inner.DialAsync(channel, context);
        public Task ListenAsync(IChannel channel, ISessionContext context) => _inner.ListenAsync(channel, context);
    }

    private sealed class ForwardedPush(IProtocolStackSettings stack, PeerStore store) : ISessionProtocol<ulong, ulong>
    {
        private readonly IdentifyPushProtocol _inner = new(stack, peerStore: store);
        public string Id => IdentifyPushProtocol.ProtocolId;
        public Task<ulong> DialAsync(IChannel channel, ISessionContext context, ulong request) => _inner.DialAsync(channel, context, request);
        public Task ListenAsync(IChannel channel, ISessionContext context) => _inner.ListenAsync(channel, context);
    }

    private sealed class SplitPushBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef? IdentifyPush() => new(new ListeningPush());
    }

    private sealed class ListeningPush : ISessionListenerProtocol
    {
        public string Id => IdentifyPushProtocol.ProtocolId;
        public Task ListenAsync(IChannel channel, ISessionContext context) => Task.CompletedTask;
    }
}
