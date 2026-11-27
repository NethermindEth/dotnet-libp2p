// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using NUnit.Framework;

namespace Libp2p.Protocols.Pubsub.E2eTests;

public class ReconnectionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReconnectsTheMeshAfterTheLastTransportCloses(bool recoverAsListener)
    {
        const string topic = "recovery";
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        RetryGate policy = new(deadline.Token);
        IServiceCollection recoveringServices = Services(autoConnect: true).AddSingleton<IPubsubReconnectPolicy>(policy);
        await using ServiceProvider recoveringProvider = recoveringServices.BuildServiceProvider();
        await using ServiceProvider passiveProvider = Services(autoConnect: false).BuildServiceProvider();
        await using ILocalPeer recovering = recoveringProvider.GetRequiredService<IPeerFactory>().Create();
        await using ILocalPeer passive = passiveProvider.GetRequiredService<IPeerFactory>().Create();
        PubsubRouter router = recoveringProvider.GetRequiredService<PubsubRouter>();
        PubsubRouter remote = passiveProvider.GetRequiredService<PubsubRouter>();
        router.GetTopic(topic);
        remote.GetTopic(topic);
        await recovering.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        await passive.StartListenAsync([(Multiaddress)"/ip4/127.0.0.1/tcp/0"], deadline.Token);
        ILocalPeer dialer = recoverAsListener ? passive : recovering;
        ILocalPeer listener = recoverAsListener ? recovering : passive;

        try
        {
            // The initial transport has a fixed direction. Identify populates the address stores
            // before the recovering router replays discovery and opens its first pubsub stream.
            ISession initial = await dialer.DialAsync(listener.ListenAddresses.Single(), deadline.Token);
            await remote.StartAsync(passive, deadline.Token);
            await router.StartAsync(recovering, deadline.Token);
            await WaitForMesh();

            await initial.DisconnectAsync().WaitAsync(deadline.Token);
            PubsubReconnectAttempt requested = await policy.Entered.Task.WaitAsync(deadline.Token);
            Assert.That(requested, Is.EqualTo(new PubsubReconnectAttempt(passive.Identity.PeerId, PubsubReconnectReason.StreamClosed, 1)));
            while (InMesh(router, passive.Identity.PeerId) || InMesh(remote, recovering.Identity.PeerId))
                await Task.Delay(5, deadline.Token);

            policy.Release.TrySetResult();
            await WaitForMesh();
            Assert.That(recovering.Sessions, Has.Some.Matches<ISession>(session => session.RemotePeerId == passive.Identity.PeerId &&
                session.IsInitialized && !session.Closed.IsCancellationRequested));
            Assert.That(initial.Closed.IsCancellationRequested, Is.True);

            TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.GetTopic(topic).OnMessage += (_, data) => received.TrySetResult(data);
            router.Publish(topic, [42]);
            Assert.That(await received.Task.WaitAsync(deadline.Token), Is.EqualTo(new byte[] { 42 }));
        }
        finally
        {
            policy.Release.TrySetResult();
            await router.DisposeAsync();
            await remote.DisposeAsync();
        }

        async Task WaitForMesh()
        {
            while (!InMesh(router, passive.Identity.PeerId) || !InMesh(remote, recovering.Identity.PeerId))
            {
                await router.Heartbeat();
                await remote.Heartbeat();
                await Task.Delay(5, deadline.Token);
            }
        }

        bool InMesh(PubsubRouter candidate, PeerId peer)
        {
            lock (candidate)
                return ((IRoutingStateContainer)candidate).Mesh.TryGetValue(topic, out HashSet<PeerId>? peers) && peers.Contains(peer);
        }
    }

    [Test]
    public async Task Test_CanTrace()
    {
        const string commonTopic = "test";
        await using PubsubE2eTestSetup test = new();
        await test.AddPeersAsync(2);
        test.Subscribe(commonTopic);
        foreach ((_, IPeerStore peerStore) in test.PeerStores.Skip(1))
            peerStore.Discover([.. test.Peers[0].ListenAddresses]);
        await test.WaitForFullMeshAsync(commonTopic);
    }

    private static IServiceCollection Services(bool autoConnect) => new ServiceCollection()
        .AddPubsub()
        .AddLibp2p(builder => builder.WithPubsub())
        .AddSingleton(new PubsubSettings { AutoConnect = autoConnect, HeartbeatInterval = int.MaxValue });

    private sealed class RetryGate(CancellationToken token) : IPubsubReconnectPolicy
    {
        public TaskCompletionSource<PubsubReconnectAttempt> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? GetRetryDelay(PubsubReconnectAttempt attempt)
        {
            Entered.TrySetResult(attempt);
            Release.Task.WaitAsync(token).GetAwaiter().GetResult();
            return TimeSpan.Zero;
        }
    }
}
