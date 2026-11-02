// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;
using System.Collections.Concurrent;
using System.Buffers;

namespace Libp2p.E2eTests;

public class MplexNegotiationTests
{
    [TestCase(0)]
    [TestCase(1)]
    public async Task FallbackNegotiatesMplexWithMplexOnlyPeer(int dialer)
    {
        await using NegotiationSetup setup = new(bothOfferYamux: false);
        await setup.AddPeersAsync(2);

        await ConnectAndPingAsync(setup, dialer, "/mplex/6.7.0");
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task BothOfferedPreferYamux(int dialer)
    {
        await using NegotiationSetup setup = new(bothOfferYamux: true);
        await setup.AddPeersAsync(2);

        await ConnectAndPingAsync(setup, dialer, "/yamux/1.0.0");
    }

    [TestCase(0)]
    [TestCase(1)]
    public async Task InboundSaturationAllowsPingAndIdentifyOnTheRegisteredStack(int dialer)
    {
        await using NegotiationSetup setup = new(bothOfferYamux: false);
        await setup.AddPeersAsync(2);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        int listener = 1 - dialer;
        TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Peers[listener].OnConnected += session => accepted.TrySetResult(session);
        ISession outbound = await setup.Peers[dialer].DialAsync([.. setup.Peers[listener].ListenAddresses], deadline.Token)
            .WaitAsync(deadline.Token);
        ISession inbound = await accepted.Task.WaitAsync(deadline.Token);
        ISession target = dialer == 0 ? outbound : inbound;
        ISession opener = dialer == 0 ? inbound : outbound;
        HoldingStreams targetHolds = setup.ServiceProviders[0].GetRequiredService<HoldingStreams>();
        HoldingStreams openerHolds = setup.ServiceProviders[1].GetRequiredService<HoldingStreams>();
        List<Task<int>> held = [];
        try
        {
            int retries = 0;
            while (held.Count < 512)
            {
                // Batch setup to avoid a transport round trip per stream while still awaiting every application.
                List<(TaskCompletionSource Ready, Task<int> Dial)> batch = [];
                int count = Math.Min(16, 512 - held.Count);
                for (int i = 0; i < count; i++)
                {
                    TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task<int> dial = opener.DialAsync<HoldingProtocol, TaskCompletionSource, int>(ready, deadline.Token);
                    held.Add(dial);
                    batch.Add((ready, dial));
                }
                foreach ((TaskCompletionSource ready, Task<int> dial) in batch)
                {
                    await Task.WhenAny(ready.Task, dial).WaitAsync(deadline.Token);
                    if (ready.Task.IsCompletedSuccessfully) continue;
                    try { await dial; }
                    catch (Exception exception) when (held.Count == 512 && targetHolds.Active == 511 && retries < 8 &&
                        !deadline.IsCancellationRequested && !target.Closed.IsCancellationRequested &&
                        exception is InvalidOperationException or Nethermind.Libp2p.Core.Exceptions.ChannelAbortedException or Nethermind.Libp2p.Core.Exceptions.ChannelClosedException)
                    {
                        // Only setup may retry while the normal startup Identify streams finish retiring.
                        // Every failed dial is observed, and control dials below are never retried.
                        TestContext.Out.WriteLine($"Holding-stream setup retry: {exception.GetType().Name}: {exception.Message}");
                        held.Remove(dial);
                        retries++;
                        await Task.Delay(10, deadline.Token);
                        continue;
                    }
                    Assert.Fail("A holding protocol completed before reporting readiness.");
                }
            }
            Assert.That(targetHolds.Active, Is.EqualTo(512), "Every held inbound stream completed application negotiation and is still live.");

            await target.DialAsync<PingProtocol>(deadline.Token).WaitAsync(deadline.Token);
            IdentifyResult identity = await target.DialAsync<IdentifyProtocol, IdentifyResult>(deadline.Token).WaitAsync(deadline.Token);
            Assert.Multiple(() =>
            {
                Assert.That(identity.PeerId, Is.EqualTo(setup.Peers[1].Identity.PeerId));
                Assert.That(identity.Protocols, Does.Contain("/ipfs/ping/1.0.0"));
                Assert.That(target.RemotePeerId, Is.EqualTo(setup.Peers[1].Identity.PeerId));
                Assert.That(opener.RemotePeerId, Is.EqualTo(setup.Peers[0].Identity.PeerId));
                Assert.That(targetHolds.Active, Is.EqualTo(512));
                Assert.That(setup.ServiceProviders[dialer].GetRequiredService<MuxerObservation>().Connections,
                    Is.EqualTo(new[] { ("/mplex/6.7.0", false) }));
                Assert.That(setup.ServiceProviders[listener].GetRequiredService<MuxerObservation>().Connections,
                    Is.EqualTo(new[] { ("/mplex/6.7.0", true) }));
            });
            await target.DialAsync<PingProtocol>(deadline.Token).WaitAsync(deadline.Token);
        }
        finally
        {
            targetHolds.Release.TrySetResult();
            openerHolds.Release.TrySetResult();
            // Join all started dials even on failure; cancellation also releases a pending negotiation.
            if (held.Any(task => task.IsFaulted) || deadline.IsCancellationRequested) deadline.Cancel();
            await Task.WhenAll(held).WaitAsync(TimeSpan.FromSeconds(10));
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(10));
            while (targetHolds.Active != 0) await Task.Delay(1, cleanup.Token);
        }
    }

    private static async Task ConnectAndPingAsync(NegotiationSetup setup, int dialer, string expectedMuxer)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        int listener = 1 - dialer;
        TaskCompletionSource<ISession> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.Peers[listener].OnConnected += session => accepted.TrySetResult(session);

        ISession outbound = await setup.Peers[dialer].DialAsync([.. setup.Peers[listener].ListenAddresses], deadline.Token)
            .WaitAsync(deadline.Token);
        ISession inbound = await accepted.Task.WaitAsync(deadline.Token);

        await Task.WhenAll(outbound.DialAsync<PingProtocol>(deadline.Token), inbound.DialAsync<PingProtocol>(deadline.Token))
            .WaitAsync(deadline.Token);

        Assert.Multiple(() =>
        {
            Assert.That(outbound.RemotePeerId, Is.EqualTo(setup.Peers[listener].Identity.PeerId));
            Assert.That(inbound.RemotePeerId, Is.EqualTo(setup.Peers[dialer].Identity.PeerId));
            Assert.That(setup.ServiceProviders[dialer].GetRequiredService<MuxerObservation>().Connections,
                Is.EqualTo(new[] { (expectedMuxer, false) }));
            Assert.That(setup.ServiceProviders[listener].GetRequiredService<MuxerObservation>().Connections,
                Is.EqualTo(new[] { (expectedMuxer, true) }));
        });
    }

    private sealed class NegotiationSetup(bool bothOfferYamux) : E2eTestSetup
    {
        private int _configuredPeers;

        protected override Multiaddress[]? GetListenAddresses(int index) => ["/ip4/127.0.0.1/tcp/0"];

        protected override IServiceCollection ConfigureServices(IServiceCollection services)
        {
            bool mplexOnly = !bothOfferYamux && _configuredPeers == 1;
            _configuredPeers++;
            return base.ConfigureServices(services)
                .AddSingleton(new MuxerObservation())
                .AddSingleton<HoldingStreams>()
                .AddSingleton<IPeerFactoryBuilder>(provider => new NegotiationBuilder(provider, mplexOnly).AddProtocol<HoldingProtocol>());
        }
    }

    private sealed class NegotiationBuilder(IServiceProvider provider, bool mplexOnly) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] StreamTransports() => [Get<IpTcpProtocol>()];
        protected override ProtocolRef[] Encryption() => [Get<NoiseProtocol>()];
        protected override ProtocolRef[] Muxers() => mplexOnly
            ? [Get<RecordingMplexProtocol>()]
            : [Get<RecordingYamuxProtocol>(), Get<RecordingMplexProtocol>()];
    }

    private sealed class HoldingStreams
    {
        private int _active;
        public int Active => Volatile.Read(ref _active);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Enter() => Interlocked.Increment(ref _active);
        public void Exit() => Interlocked.Decrement(ref _active);
    }

    private sealed class HoldingProtocol(HoldingStreams streams) : ISessionProtocol<TaskCompletionSource, int>
    {
        public string Id => "/mplex-hold/1.0.0";

        public async Task<int> DialAsync(IChannel channel, ISessionContext context, TaskCompletionSource ready)
        {
            Assert.That((await channel.ReadAsync(1).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 1 }));
            ready.TrySetResult();
            await streams.Release.Task;
            return 1;
        }

        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            streams.Enter();
            try
            {
                await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).OrThrow();
                await streams.Release.Task;
            }
            finally { streams.Exit(); }
        }
    }

    private sealed class MuxerObservation
    {
        public ConcurrentQueue<(string Protocol, bool IsListener)> Connections { get; } = new();
    }

    private sealed class RecordingMplexProtocol(MuxerObservation observation, MultiplexerSettings? settings = null,
        ILoggerFactory? loggerFactory = null) : MplexProtocol(settings, loggerFactory)
    {
        protected override Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
        {
            observation.Connections.Enqueue((Id, isListener));
            return base.ConnectAsync(channel, context, isListener);
        }
    }

    private sealed class RecordingYamuxProtocol(MuxerObservation observation, MultiplexerSettings? settings = null,
        ILoggerFactory? loggerFactory = null) : YamuxProtocol(settings, loggerFactory)
    {
        protected override Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
        {
            observation.Connections.Enqueue((Id, isListener));
            return base.ConnectAsync(channel, context, isListener);
        }
    }
}
