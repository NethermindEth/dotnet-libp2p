// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NUnit.Framework;

namespace Nethermind.Libp2p.E2eTests;

public class ConnectionTimeoutTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly PeerId RemotePeer = TestPeers.PeerId(2);
    private static readonly Multiaddress FirstAddress = $"/ip4/127.0.0.1/tcp/4001/p2p/{RemotePeer}";
    private static readonly Multiaddress SecondAddress = $"/ip4/127.0.0.1/tcp/4002/p2p/{RemotePeer}";

    [TestCase(null, 15)]
    [TestCase(2, 2)]
    [TestCase(30, 30)]
    public async Task SharedDeadlineCoversIdentifyAndCancelsEveryAddress(int? configuredSeconds, int expectedSeconds)
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new();
        using ServiceProvider services = CreateServices(clock, transport, configuredSeconds);
        await using ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));
        using CancellationTokenSource caller = new();
        Task<ISession> dial = peer.DialAsync([FirstAddress, SecondAddress], caller.Token);
        CancellationToken firstAttempt = await transport.First.Started.Task.WaitAsync(TestTimeout);
        CancellationToken secondAttempt = await transport.Second.Started.Task.WaitAsync(TestTimeout);
        transport.First.AllowUpgrade.SetResult();
        INewSessionContext setup = await transport.First.Session.Task.WaitAsync(TestTimeout);
        ISession initializing = peer.Sessions.Single();
        UpgradeOptions identify = await NextRequestAsync(setup);
        Assert.That(identify.SelectedProtocol, Is.TypeOf<IdentifyProtocol>());

        TimeSpan budget = TimeSpan.FromSeconds(expectedSeconds);
        Assert.That(clock.ActiveTimersFor(budget), Is.EqualTo(1));
        clock.Advance(budget - TimeSpan.FromTicks(1));
        Assert.That(dial.IsCompleted, Is.False);

        Task<ISession> joined = peer.DialAsync(RemotePeer);
        caller.Cancel();
        OperationCanceledException? cancellation = Assert.CatchAsync<OperationCanceledException>(async () => await dial.WaitAsync(TestTimeout));
        Assert.That(cancellation!.CancellationToken, Is.EqualTo(caller.Token));
        Assert.That(joined.IsCompleted, Is.False);
        Assert.That(transport.First.Stopped.Task.IsCompleted || transport.Second.Stopped.Task.IsCompleted, Is.False);

        clock.Advance(TimeSpan.FromTicks(1));
        await Task.WhenAny(joined).WaitAsync(TestTimeout);
        Assert.ThrowsAsync<TimeoutException>(async () => await joined);

        await Task.WhenAll(transport.First.Stopped.Task, transport.Second.Stopped.Task).WaitAsync(TestTimeout);
        Assert.That(firstAttempt.IsCancellationRequested, Is.True);
        Assert.That(secondAttempt.IsCancellationRequested, Is.True);
        Assert.That(setup.Token.IsCancellationRequested, Is.True);
        await initializing.DisconnectAsync().WaitAsync(TestTimeout);
        Assert.That(peer.Sessions, Is.Empty);
        Assert.That(clock.ActiveTimers, Is.Zero);
    }

    [Test]
    public async Task WinningAddressRemainsUsableAfterItsDeadlinePasses()
    {
        ManualTimeProvider clock = new();
        GatedTransport transport = new();
        using ServiceProvider services = CreateServices(clock, transport, configuredSeconds: 2);
        await using ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));
        Task<ISession> dial = peer.DialAsync([FirstAddress, SecondAddress], CancellationToken.None);
        CancellationToken losingAttempt = await transport.First.Started.Task.WaitAsync(TestTimeout);
        CancellationToken winningAttempt = await transport.Second.Started.Task.WaitAsync(TestTimeout);
        transport.Second.AllowUpgrade.SetResult();
        INewSessionContext connection = await transport.Second.Session.Task.WaitAsync(TestTimeout);
        UpgradeOptions identify = await NextRequestAsync(connection);
        Assert.That(identify.SelectedProtocol, Is.TypeOf<IdentifyProtocol>());
        identify.CompletionSource!.SetResult(null);
        ISession session = await dial.WaitAsync(TestTimeout);
        Assert.That(session.RemoteAddress, Is.EqualTo(SecondAddress));

        await transport.First.Stopped.Task.WaitAsync(TestTimeout);
        Assert.That(losingAttempt.IsCancellationRequested, Is.True);
        Assert.That(clock.ActiveTimers, Is.Zero);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.That(winningAttempt.IsCancellationRequested, Is.False);
        Assert.That(session.Closed.IsCancellationRequested, Is.False);
        Task ping = session.DialAsync<PingProtocol>();
        UpgradeOptions request = await NextRequestAsync(connection);
        Channel channel = new();
        Task exchange = connection.Upgrade(channel, request.SelectedProtocol!, request);
        ReadResult payload = await channel.Reverse.ReadAsync(32).AsTask().WaitAsync(TestTimeout);
        Assert.That(payload.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(await channel.Reverse.WriteAsync(payload.Data).AsTask().WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));
        await Task.WhenAll(exchange, ping).WaitAsync(TestTimeout);
        await session.DisconnectAsync();
        await transport.Second.Stopped.Task.WaitAsync(TestTimeout);
    }

    private static ServiceProvider CreateServices(ManualTimeProvider clock, GatedTransport transport, int? configuredSeconds)
        => new ServiceCollection()
            .AddLibp2p<DeadlineBuilder>()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(transport)
            .AddSingleton(new PeerResourceLimits(initializationTimeout: TimeSpan.FromSeconds(60),
                connectionTimeout: configuredSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null))
            .BuildServiceProvider();

    private static Task<UpgradeOptions> NextRequestAsync(INewSessionContext session)
        => Task.Run(() => session.DialRequests.First()).WaitAsync(TestTimeout);

    private sealed class DeadlineBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
        {
            ProtocolRef transport = Get<GatedTransport>();
            Connect(transport, [Get<IdentifyProtocol>(), Get<PingProtocol>()]);
            return [transport];
        }
    }

    private sealed class GatedTransport : ITransportProtocol
    {
        public string Id => "/test/connection-timeout";
        public Attempt First { get; } = new();
        public Attempt Second { get; } = new();

        public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [];
        public static bool IsAddressMatch(Multiaddress addr) => addr == FirstAddress || addr == SecondAddress;

        public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
        {
            Attempt attempt = remoteAddr == FirstAddress ? First : Second;
            try
            {
                using INewConnectionContext connection = context.CreateConnection();
                connection.State.RemoteAddress = remoteAddr;
                using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, connection.Token);
                attempt.Started.SetResult(token);
                await attempt.AllowUpgrade.Task.WaitAsync(lifetime.Token);
                using INewSessionContext session = connection.UpgradeToSession();
                attempt.Session.SetResult(session);
                await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
            }
            finally { attempt.Stopped.SetResult(); }
        }

        public Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Attempt
    {
        public TaskCompletionSource<CancellationToken> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowUpgrade { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<INewSessionContext> Session { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
