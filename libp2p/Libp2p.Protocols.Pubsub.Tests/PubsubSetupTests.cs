// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nethermind.Libp2p.Core.Discovery;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

public class PubsubSetupTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly string[] ProtocolIds = [
        "/meshsub/1.3.0", "/meshsub/1.2.0", "/meshsub/1.1.0", "/meshsub/1.0.0", "/floodsub/1.0.0",
    ];

    [Test]
    public async Task AddLibp2pDoesNotRegisterOrSelectPubsub()
    {
        await using ServiceProvider services = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        _ = services.GetRequiredService<IPeerFactory>();

        Assert.Multiple(() =>
        {
            Assert.That(services.GetService<PubsubRouter>(), Is.Null);
            Assert.That(GetPubsubProtocols(services), Is.Empty);
        });
    }

    [Test]
    public async Task AddPubsubRegistersOneRouterWithoutSelectingHandlers()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p()
            .AddPubsub()
            .AddPubsub()
            .BuildServiceProvider();
        PubsubRouter router = services.GetRequiredService<PubsubRouter>();
        _ = services.GetRequiredService<IPeerFactory>();

        Assert.Multiple(() =>
        {
            Assert.That(services.GetServices<PubsubRouter>(), Is.EqualTo(new[] { router }));
            Assert.That(GetPubsubProtocols(services), Is.Empty);
            Assert.Throws<InvalidOperationException>(() => router.Publish("topic", [1]));
        });
    }

    [Test]
    public async Task SelectingPubsubWithoutRouterRegistrationFailsAtBuild()
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub())
            .BuildServiceProvider();

        InvalidOperationException? failure = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IPeerFactory>());
        Assert.That(failure!.Message, Does.Contain("AddPubsub()"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RepeatedSelectionUsesTheConfiguredRouter(bool genericBuilder)
    {
        PubsubSettings settings = new() { AutoConnect = false, MaxRpcBytes = 4_096 };
        IServiceCollection registrations = new ServiceCollection().AddPubsub().AddSingleton(settings);
        if (genericBuilder)
            registrations.AddLibp2p<Libp2pPeerFactoryBuilder>(builder => ((Libp2pPeerFactoryBuilder)builder).WithPubsub().WithPubsub());
        else
            registrations.AddLibp2p(builder => builder.WithPubsub().WithPubsub());

        await using ServiceProvider services = registrations.AddPubsub().BuildServiceProvider();
        PubsubRouter router = services.GetRequiredService<PubsubRouter>();
        await using ILocalPeer localPeer = services.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(0));
        Assert.Multiple(() =>
        {
            Assert.That(services.GetServices<PubsubRouter>(), Is.EqualTo(new[] { router }));
            Assert.That(router.MaxRpcBytes, Is.EqualTo(settings.MaxRpcBytes));
            Assert.Throws<InvalidOperationException>(() => router.Publish("topic", [1]));
        });

        await router.StartAsync(localPeer);
        await AssertHandlersUseRouter(services, router);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AddLibp2pPreservesCustomRouter(bool registerCustomFirst)
    {
        await using PubsubRouter router = new(new PeerStore(), new PubsubSettings { AutoConnect = false });
        IServiceCollection registrations = new ServiceCollection();
        if (registerCustomFirst) registrations.AddSingleton(router);
        registrations.AddPubsub().AddLibp2p(builder => builder.WithPubsub());
        if (!registerCustomFirst) registrations.Replace(ServiceDescriptor.Singleton(router));

        await using (ServiceProvider services = registrations.BuildServiceProvider())
        {
            _ = services.GetRequiredService<IPeerFactory>();
            Assert.Multiple(() =>
            {
                Assert.That(services.GetServices<PubsubRouter>(), Is.EqualTo(new[] { router }));
                Assert.That(services.GetRequiredService<PubsubRouter>(), Is.SameAs(router));
            });
            await AssertHandlersUseRouter(services, router);
        }

        Assert.That(router.Stopped.IsCancellationRequested, Is.False);
    }

    [Test]
    public async Task StandaloneBuilderOwnsTheRouterRegisteredInItsServices()
    {
        PubsubRouter router;
        await using (Libp2pPeerFactoryBuilder builder = new())
        {
            builder.Services.AddPubsub();
            builder.WithPubsub();
            builder.Build();
            router = builder.ServiceProvider.GetRequiredService<PubsubRouter>();

            await AssertHandlersUseRouter(builder.ServiceProvider, router);
        }

        Assert.That(router.Stopped.IsCancellationRequested, Is.True);
    }

    [Test]
    public async Task StartAsyncStartsCacheSweepingAndProviderDisposalWaitsForIt()
    {
        SweepClock clock = new();
        ServiceCollection registrations = new();
        registrations.AddSingleton<TimeProvider>(clock)
            .AddSingleton(new PubsubSettings { AutoConnect = false, HeartbeatInterval = int.MaxValue })
            .AddPubsub()
            .AddLibp2p(builder => builder.WithPubsub());
        Assert.That(clock.Tick.Task.IsCompleted, Is.False);

        await using ServiceProvider services = registrations.BuildServiceProvider();
        PubsubRouter router = services.GetRequiredService<PubsubRouter>();
        await using ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(0));
        Assert.That(clock.Tick.Task.IsCompleted, Is.False);
        Assert.Throws<InvalidOperationException>(() => router.Publish("topic", [1]));

        await router.StartAsync(peer);
        Action tick = await clock.Tick.Task.WaitAsync(TestTimeout);
        Task fire = Task.Run(tick);
        Task disposal = Task.CompletedTask;
        try
        {
            await clock.Sweeping.Task.WaitAsync(TestTimeout);
            disposal = services.DisposeAsync().AsTask();
            Assert.That(disposal.IsCompleted, Is.False);
        }
        finally
        {
            clock.Release.TrySetResult();
            await Task.WhenAll(fire, disposal).WaitAsync(TestTimeout);
        }
    }

    private static async Task AssertHandlersUseRouter(IServiceProvider services, PubsubRouter router)
    {
        ProtocolRef[] protocols = GetPubsubProtocols(services).ToArray();
        Assert.That(protocols.Select(p => p.Id), Is.EquivalentTo(ProtocolIds));

        for (int i = 0; i < protocols.Length; i++)
        {
            ISessionContext context = Substitute.For<ISessionContext>();
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(i + 1) });
            Channel channel = new();
            Task listen = ((PubsubProtocol)protocols[i].Protocol).ListenAsync(channel, context);
            try
            {
                lock (router)
                    Assert.That(((IRoutingStateContainer)router).ConnectedPeers, Does.Contain(context.State.RemotePeerId));
            }
            finally
            {
                await channel.AbortAsync();
                await listen.WaitAsync(TestTimeout);
            }
        }
    }

    private static IEnumerable<ProtocolRef> GetPubsubProtocols(IServiceProvider services) =>
        services.GetRequiredService<IProtocolStackSettings>().Protocols!.Keys.Where(p => p.Protocol is PubsubProtocol);

    private sealed class SweepClock : TimeProvider
    {
        public TaskCompletionSource<Action> Tick { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Sweeping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.FromSeconds(5))
                Tick.TrySetResult(() => callback(state));
            return Substitute.For<ITimer>();
        }

        public override DateTimeOffset GetUtcNow()
        {
            Sweeping.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return base.GetUtcNow();
        }
    }
}
