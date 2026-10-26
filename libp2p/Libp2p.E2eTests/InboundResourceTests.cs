// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net.Sockets;
using NUnit.Framework;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address.Net;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.E2eTests;

public class InboundResourceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task RegisteredTcpStackClosesSilentHandshakes(bool plaintext)
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p(b => plaintext ? b.WithPlaintextEnforced() : b).BuildServiceProvider();
        await using ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create();
        using CancellationTokenSource lifetime = new();
        await peer.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], lifetime.Token);
        try
        {
            await Task.WhenAll(plaintext
                ? [Probe(null), Probe("/plaintext/2.0.0")]
                : new[] { Probe(null), Probe("/noise"), Probe("/tls/1.0.0"), Probe("/noise", [0]), Probe("/noise", [0, 32, 0]) });
        }
        finally { lifetime.Cancel(); }

        async Task Probe(string? security, byte[]? partial = null)
        {
            using TcpClient remote = new();
            await remote.ConnectAsync(peer.ListenAddresses.Single().ToEndPoint());
            using NetworkStream stream = remote.GetStream();
            if (security is not null)
            {
                Assert.That(await ReadLine(stream), Is.EqualTo("/multistream/1.0.0"));
                await WriteLine(stream, "/multistream/1.0.0");
                await WriteLine(stream, security);
                Assert.That(await ReadLine(stream), Is.EqualTo(security));
            }
            if (partial is not null) await stream.WriteAsync(partial);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(18));
            byte[] data = new byte[1024];
            try
            {
                while (await stream.ReadAsync(data, deadline.Token) != 0) { }
            }
            catch (System.IO.IOException) { }
            Assert.That(deadline.IsCancellationRequested, Is.False, $"Silent {security ?? "multistream"} must be disconnected.");
        }
    }

    [Test]
    public void RegisteredBuiltInProtocolsHaveFiniteLimits()
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p(builder => builder.WithPubsub().WithRelay()).BuildServiceProvider();
        _ = services.GetRequiredService<IPeerFactory>();
        var protocols = services.GetRequiredService<IProtocolStackSettings>().Protocols!.Keys;
        foreach (ProtocolRef protocol in protocols.Where(p => p.Protocol is ISessionListenerProtocol or ISessionProtocol))
        {
            Assert.That(protocol.InboundLimit, Is.Not.Null, protocol.Id);
            Assert.That(protocol.InboundLimit!.MaxConcurrent, Is.EqualTo(512), protocol.Id);
            Assert.That(protocol.InboundLimit.MaxPerPeer, Is.EqualTo(64), protocol.Id);
        }
        ProtocolRef[] pubsub = protocols.Where(p => p.Protocol is PubsubProtocol).ToArray();
        Assert.That(pubsub, Has.Length.EqualTo(5));
        Assert.That(pubsub.Select(p => p.InboundLimit).Distinct().Count(), Is.EqualTo(1));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task RegisteredIdentifyInitializationExpiresAndRestoresStoreCapacity(bool inbound, bool stallResponse)
    {
        PeerStore store = new(3);
        using ServiceProvider services = new ServiceCollection().AddLibp2p()
            .AddSingleton(store)
            .AddSingleton(new PeerResourceLimits(initializationTimeout: TimeSpan.FromMilliseconds(500)))
            .BuildServiceProvider();
        await using ILocalPeer target = services.GetRequiredService<IPeerFactory>().Create();
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        await target.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], lifetime.Token);
        List<LocalPeer> attackers = [];
        List<PeerStore.PeerInfo> retained = [];
        List<Task<ISession>> dials = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                using ServiceProvider attackerServices = new ServiceCollection().AddLibp2p().BuildServiceProvider();
                _ = attackerServices.GetRequiredService<IPeerFactory>();
                IProtocolStackSettings stack = attackerServices.GetRequiredService<IProtocolStackSettings>();
                StallingSelector stall = new(stallResponse);
                ProtocolRef selector = new(stall);
                ProtocolRef muxer = stack.Protocols!.Keys.Single(p => p.Protocol is YamuxProtocol);
                ProtocolRef originalSelector = stack.Protocols[muxer].Single();
                stack.Protocols[selector] = stack.Protocols[originalSelector];
                stack.Protocols[muxer] = [selector];
                LocalPeer attacker = new(new Identity(), new PeerStore(), stack);
                attackers.Add(attacker);
                Task<ISession> dial;
                if (inbound)
                {
                    ISession remote = await attacker.DialAsync([.. target.ListenAddresses], lifetime.Token);
                    // A stream SYN makes the listener's Yamux create its session and start Identify.
                    _ = remote.DialAsync<PingProtocol>(lifetime.Token).ContinueWith(t => _ = t.Exception);
                    dial = Task.FromResult(remote);
                }
                else
                {
                    await attacker.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], lifetime.Token);
                    dial = target.DialAsync([.. attacker.ListenAddresses], lifetime.Token);
                }
                dials.Add(dial);
                await stall.Entered.Task.WaitAsync(lifetime.Token);
                PeerStore.PeerInfo info = store.GetPeerInfo(attacker.Identity.PeerId);
                Assert.That(info.Sessions, Is.EqualTo(1), "The real secure/multiplexed initialization retained this peer.");
                retained.Add(info);
                await stall.Stopped.Task.WaitAsync(lifetime.Token);
                if (!inbound) Assert.CatchAsync<Exception>(async () => await dial);
                // Remote teardown precedes pin release; target-side removal follows release.
                var targetSessions = ((LocalPeer)target).Sessions;
                Assert.That(() => { lock (targetSessions) return targetSessions.Count; }, Is.Zero.After(5000, 20),
                    "The target must finish disconnecting before checking peer-store retention.");
                Assert.That(info.Sessions, Is.Zero, "Expiry must release the pin and terminate the remote protocol work.");
            }
            Assert.That(retained.All(info => info.Sessions == 0), Is.True);
            Assert.That(((LocalPeer)target).Sessions, Is.Empty);
            using ServiceProvider healthyServices = new ServiceCollection().AddLibp2p().BuildServiceProvider();
            await using ILocalPeer healthy = healthyServices.GetRequiredService<IPeerFactory>().Create();
            ISession healthySession = await healthy.DialAsync([.. target.ListenAddresses], lifetime.Token);
            await healthySession.DialAsync<PingProtocol>(lifetime.Token);
        }
        finally
        {
            lifetime.Cancel();
            foreach (LocalPeer attacker in attackers) await attacker.DisposeAsync();
            await ((Task)Task.WhenAll(dials)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task UpgradeDeadlineIncludesAuthenticatedYamuxBeforeSessionTraffic()
    {
        using ServiceProvider services = new ServiceCollection().AddLibp2p()
            .AddSingleton(new PeerResourceLimits(inboundUpgradeTimeout: TimeSpan.FromMilliseconds(500)))
            .BuildServiceProvider();
        await using ILocalPeer target = services.GetRequiredService<IPeerFactory>().Create();
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        await target.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], lifetime.Token);
        using ServiceProvider remoteServices = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        _ = remoteServices.GetRequiredService<IPeerFactory>();
        IProtocolStackSettings stack = remoteServices.GetRequiredService<IProtocolStackSettings>();
        SilentMuxer silent = new();
        ProtocolRef muxer = stack.Protocols!.Keys.Single(p => p.Protocol is YamuxProtocol);
        ProtocolRef replacement = new(silent);
        foreach (ProtocolRef parent in stack.Protocols.Keys.ToArray())
            stack.Protocols[parent] = stack.Protocols[parent].Select(p => p == muxer ? replacement : p).ToArray();
        stack.Protocols[replacement] = [];
        await using LocalPeer remote = new(new Identity(), null, stack);
        Task<ISession> dial = remote.DialAsync([.. target.ListenAddresses], lifetime.Token);
        try
        {
            await silent.Entered.Task.WaitAsync(lifetime.Token);
            await silent.Stopped.Task.WaitAsync(lifetime.Token);
            Assert.That(((LocalPeer)target).Sessions, Is.Empty);
            Assert.CatchAsync<Exception>(async () => await dial.WaitAsync(lifetime.Token));
        }
        finally { lifetime.Cancel(); await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing); }
    }

    [Test]
    public async Task RegisteredPingEnforcesConfiguredPeerAndTotalLimitsAndRecovers()
    {
        InboundStreamLimit limit = new(2, 1);
        using ServiceProvider services = new ServiceCollection().AddLibp2p()
            .AddSingleton(new BuiltInProtocolLimits { Ping = limit }).BuildServiceProvider();
        await using ILocalPeer target = services.GetRequiredService<IPeerFactory>().Create();
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15));
        await target.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], lifetime.Token);
        List<ILocalPeer> clients = [];
        List<Task> calls = [];
        List<HoldingPing> handlers = [];
        try
        {
            for (int i = 0; i < 3; i++)
            {
                HoldingPing handler = new();
                handlers.Add(handler);
                using ServiceProvider clientServices = new ServiceCollection().AddLibp2p(b => b.AddProtocol(handler)).BuildServiceProvider();
                ILocalPeer client = clientServices.GetRequiredService<IPeerFactory>().Create();
                clients.Add(client);
                ISession session = await client.DialAsync([.. target.ListenAddresses], lifetime.Token);
                Task call = session.DialAsync(handler, lifetime.Token);
                calls.Add(call);
                if (i < 2)
                {
                    await handler.Entered.Task.WaitAsync(lifetime.Token);
                    Assert.CatchAsync<Exception>(async () => await session.DialAsync(handler, lifetime.Token).WaitAsync(lifetime.Token));
                }
                else Assert.CatchAsync<Exception>(async () => await call.WaitAsync(lifetime.Token));
            }
            Assert.That(limit.InFlight, Is.EqualTo(2));
            handlers[0].Release.TrySetResult();
            await calls[0].WaitAsync(lifetime.Token);
            while (limit.InFlight != 1) await Task.Delay(5, lifetime.Token);
            ISession recovered = await clients[2].DialAsync([.. target.ListenAddresses], lifetime.Token);
            await recovered.DialAsync<PingProtocol>(lifetime.Token);
        }
        finally
        {
            foreach (HoldingPing handler in handlers) handler.Release.TrySetResult();
            lifetime.Cancel();
            foreach (ILocalPeer client in clients) await client.DisposeAsync();
            await Task.WhenAll(calls).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("/ip4/127.0.0.1/tcp/0")]
    [TestCase("/ip6/::1/tcp/0")]
    public async Task ProductionTcpAdmissionIsIndependentPerLocalPeerAndRecoversAfterExpiry(string address)
    {
        if (address.StartsWith("/ip6") && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
        using ServiceProvider services = new ServiceCollection().AddLibp2p()
            .AddSingleton(new PeerResourceLimits(1, 1, inboundUpgradeTimeout: TimeSpan.FromSeconds(2)))
            .BuildServiceProvider();
        IPeerFactory factory = services.GetRequiredService<IPeerFactory>();
        await using ILocalPeer first = factory.Create();
        await using ILocalPeer second = factory.Create();
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(12));
        await first.StartListenAsync([address], lifetime.Token);
        await second.StartListenAsync([address], lifetime.Token);
        try
        {
            using TcpClient firstSocket = new();
            using TcpClient secondSocket = new();
            await firstSocket.ConnectAsync(first.ListenAddresses.Single().ToEndPoint(), lifetime.Token);
            Assert.That(await ReadLine(firstSocket.GetStream()), Is.EqualTo("/multistream/1.0.0"));
            await secondSocket.ConnectAsync(second.ListenAddresses.Single().ToEndPoint(), lifetime.Token);
            Assert.That(await ReadLine(secondSocket.GetStream()), Is.EqualTo("/multistream/1.0.0"),
                "Peers sharing the production transport instance must have separate admission scopes.");
            using TcpClient refused = new();
            await refused.ConnectAsync(first.ListenAddresses.Single().ToEndPoint(), lifetime.Token);
            Assert.That(await ReadOrReset(refused), Is.Zero);
            Assert.That(await ReadOrReset(firstSocket), Is.Zero, "The upgrade deadline closes the admitted silent socket.");
            // Socket closure can precede protocol teardown. Admission stays charged until teardown ends.
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                using TcpClient recovered = new();
                await recovered.ConnectAsync(first.ListenAddresses.Single().ToEndPoint(), lifetime.Token);
                try
                {
                    Assert.That(await ReadLine(recovered.GetStream()), Is.EqualTo("/multistream/1.0.0"));
                    break;
                }
                catch (IOException) { await Task.Delay(10, lifetime.Token); }
            }
        }
        finally { lifetime.Cancel(); }

        async Task<int> ReadOrReset(TcpClient client)
        {
            try { return await client.GetStream().ReadAsync(new byte[1], lifetime.Token); }
            catch (IOException) { return 0; }
        }
    }

    private sealed class HoldingPing : ISessionProtocol
    {
        public string Id => "/ipfs/ping/1.0.0";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            await channel.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(new byte[32]), context.Token);
            await channel.ReadAsync(32, token: context.Token).OrThrow();
            Entered.TrySetResult();
            await Release.Task.WaitAsync(context.Token);
        }
        public Task ListenAsync(IChannel channel, ISessionContext context) => new PingProtocol().ListenAsync(channel, context);
    }

    private sealed class SilentMuxer : IConnectionProtocol
    {
        public string Id => "/yamux/1.0.0";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task DialAsync(IChannel channel, IConnectionContext context)
        {
            Entered.TrySetResult();
            try { await channel.ReadAsync(1, token: context.Token); }
            finally { Stopped.TrySetResult(); }
        }
        public Task ListenAsync(IChannel channel, IConnectionContext context) => DialAsync(channel, context);
    }

    private sealed class StallingSelector(bool response) : IConnectionProtocol
    {
        public string Id => "/multistream/1.0.0";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DialAsync(IChannel channel, IConnectionContext context) => new MultistreamProtocol().DialAsync(channel, context);
        public async Task ListenAsync(IChannel channel, IConnectionContext context)
        {
            try
            {
                Assert.That(await channel.ReadLineAsync(1024, context.Token), Is.EqualTo(Id));
                Assert.That(await channel.ReadLineAsync(1024, context.Token), Is.EqualTo(IdentifyProtocol.ProtocolId));
                if (response)
                {
                    await channel.WriteLineAsync(Id, context.Token);
                    await channel.WriteLineAsync(IdentifyProtocol.ProtocolId, context.Token);
                }
                Entered.TrySetResult();
                await channel.ReadAsync(1, token: context.Token);
            }
            finally { Stopped.TrySetResult(); }
        }
    }

    internal static async Task WriteLine(NetworkStream stream, string line)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(new[] { (byte)bytes.Length });
        await stream.WriteAsync(bytes);
    }

    internal static async Task<string> ReadLine(NetworkStream stream)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        byte[] prefix = new byte[1];
        await stream.ReadExactlyAsync(prefix, deadline.Token);
        byte[] data = new byte[prefix[0]];
        await stream.ReadExactlyAsync(data, deadline.Token);
        return System.Text.Encoding.UTF8.GetString(data).TrimEnd('\n');
    }
}
