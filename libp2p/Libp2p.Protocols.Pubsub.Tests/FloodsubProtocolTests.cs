// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.Pubsub.Tests;

[TestFixture]
public class FloodsubProtocolTests
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Test_Peer_is_in_floodsub_peers()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        FloodsubEndpoint endpoint = new(deadline.Token);
        await using ServiceProvider localServices = new ServiceCollection()
            .AddLibp2p(builder => builder.WithPubsub())
            .AddSingleton(new PubsubSettings { HeartbeatInterval = int.MaxValue, ReconnectionPeriod = int.MaxValue })
            .BuildServiceProvider();
        // Identify advertises floodsub as the remote's only pubsub protocol.
        await using ServiceProvider remoteServices = new ServiceCollection()
            .AddLibp2p(builder => builder.AddProtocol(endpoint))
            .BuildServiceProvider();
        ILocalPeer localPeer = localServices.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(2));
        ILocalPeer remotePeer = remoteServices.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));
        ConcurrentBag<ISession> sessions = [];
        localPeer.OnConnected += sessions.Add;
        remotePeer.OnConnected += sessions.Add;
        PubsubRouter router = localServices.GetRequiredService<PubsubRouter>();
        IRoutingStateContainer state = router;
        PeerStore peerStore = localServices.GetRequiredService<PeerStore>();
        const string commonTopic = "topic1";
        byte[] marker = "remote subscription processed"u8.ToArray();
        byte[] payload = "local floodsub publication"u8.ToArray();
        TaskCompletionSource<(PeerId Peer, byte[] Data)> delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await remotePeer.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token).WaitAsync(deadline.Token);
            await router.StartAsync(localPeer, deadline.Token);
            ITopic topic = router.GetTopic(commonTopic);
            topic.OnMessage += (peer, data) => delivered.TrySetResult((peer, data));
            lock (router) Assert.That(state.FloodsubPeers, Is.Empty);

            // Exercise automatic discovery, Identify, fallback selection and the registered protocol.
            peerStore.Discover([remotePeer.ListenAddresses.Single()]);
            Rpc snapshot = await endpoint.WaitAsync(endpoint.Subscription.Task, "the local subscription snapshot");
            Assert.That(snapshot.Subscriptions.Select(s => (s.Topicid, s.Subscribe)),
                Is.EqualTo(new[] { (commonTopic, true) }));
            string[] protocols = peerStore.GetPeerInfo(remotePeer.Identity.PeerId).SupportedProtocols ?? [];
            Assert.That(protocols
                .Where(p => p == PubsubRouter.FloodsubProtocolVersion || p.StartsWith("/meshsub/", StringComparison.Ordinal)),
                Is.EqualTo(new[] { PubsubRouter.FloodsubProtocolVersion }));

            await endpoint.SendAsync(new Rpc().WithTopics([commonTopic], []));
            await endpoint.SendAsync(new Rpc().WithMessages(commonTopic, 1, remotePeer.Identity.PeerId.Bytes, marker, remotePeer.Identity));
            // Both RPCs use the same inbound stream. Delivery is a barrier after subscription processing.
            (PeerId sender, byte[] data) = await endpoint.WaitAsync(delivered.Task, "the signed remote marker delivery");
            Assert.Multiple(() =>
            {
                Assert.That(sender, Is.EqualTo(remotePeer.Identity.PeerId));
                Assert.That(data, Is.EqualTo(marker));
            });
            lock (router)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(state.FloodsubPeers[commonTopic], Is.EquivalentTo(new[] { remotePeer.Identity.PeerId }));
                    Assert.That(state.ConnectedPeers, Is.EquivalentTo(new[] { remotePeer.Identity.PeerId }));
                    Assert.That(state.GossipsubPeers, Is.Empty);
                    Assert.That(state.Mesh[commonTopic], Is.Empty);
                });
            }

            topic.Publish(payload);
            Rpc received = await endpoint.WaitAsync(endpoint.Publication.Task, "the local publication at the remote endpoint");
            Assert.That(received.Publish, Has.Count.EqualTo(1));
            Message message = received.Publish.Single();
            Assert.Multiple(() =>
            {
                Assert.That(message.Topic, Is.EqualTo(commonTopic));
                Assert.That(message.Data.ToByteArray(), Is.EqualTo(payload));
                Assert.That(message.From.ToByteArray(), Is.EqualTo(localPeer.Identity.PeerId.Bytes));
                Assert.That(message.VerifySignature(PubsubSettings.SignaturePolicy.StrictSign), Is.True);
            });
        }
        finally
        {
            endpoint.Stop();
            deadline.Cancel();
            router.Dispose();
            try
            {
                await Task.WhenAll([
                    router.DisposeAsync().AsTask(),
                    .. sessions.Select(session => session.DisconnectAsync()),
                    localPeer.DisposeAsync().AsTask(),
                    remotePeer.DisposeAsync().AsTask(),
                ]).WaitAsync(CleanupTimeout);
            }
            finally
            {
                await endpoint.DrainAsync().WaitAsync(CleanupTimeout);
            }
        }
    }

    // A wire endpoint, not a second router: membership and forwarding must come from the local production stack.
    private sealed class FloodsubEndpoint(CancellationToken token) : ISessionProtocol
    {
        private readonly List<Task> _tasks = [];
        private readonly TaskCompletionSource<IChannel> _reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _stopping;

        public string Id => PubsubRouter.FloodsubProtocolVersion;
        public TaskCompletionSource<Rpc> Subscription { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Rpc> Publication { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DialAsync(IChannel channel, ISessionContext context) => Track(async () =>
        {
            _reply.TrySetResult(channel);
            await channel;
        });

        public Task ListenAsync(IChannel channel, ISessionContext context) => Track(async () =>
        {
            Subscription.TrySetResult(await channel.ReadPrefixedProtobufAsync(Rpc.Parser, token));
            // Open the reciprocal direction on this session only after the production router dials us.
            _ = Track(() => context.DialAsync(this));
            while (!token.IsCancellationRequested)
            {
                Rpc rpc = await channel.ReadPrefixedProtobufAsync(Rpc.Parser, token);
                if (rpc.Publish.Count != 0) Publication.TrySetResult(rpc);
            }
        });

        public async Task SendAsync(Rpc rpc)
        {
            IChannel channel = await WaitAsync(_reply.Task, "the reciprocal floodsub stream");
            Task write = channel.WriteSizeAndProtobufAsync(rpc).AsTask();
            lock (_tasks) _tasks.Add(write);
            await write.WaitAsync(token);
        }

        public async Task<T> WaitAsync<T>(Task<T> signal, string operation)
        {
            try
            {
                Task completed = await Task.WhenAny(signal, _failure.Task).WaitAsync(token);
                if (completed == _failure.Task)
                    throw new InvalidOperationException($"Remote floodsub failed while waiting for {operation}.", await _failure.Task);
                return await signal;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for {operation}.");
            }
        }

        public void Stop()
        {
            lock (_tasks) _stopping = true;
        }

        public Task DrainAsync()
        {
            lock (_tasks) return Task.WhenAll(_tasks);
        }

        private Task Track(Func<Task> run)
        {
            lock (_tasks)
            {
                // Stop closes admission before cleanup snapshots tasks, including on assertion failure.
                if (_stopping) return Task.CompletedTask;
                Task task = ObserveAsync(run);
                _tasks.Add(task);
                return task;
            }
        }

        private async Task ObserveAsync(Func<Task> run)
        {
            try
            {
                await run();
                if (!_stopping && !token.IsCancellationRequested) throw new IOException("Floodsub stream ended before test cleanup.");
            }
            catch (OperationCanceledException) when (_stopping || token.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException) when (_stopping)
            {
            }
            catch (Exception ex)
            {
                _failure.TrySetResult(ex);
                throw;
            }
        }
    }
}
