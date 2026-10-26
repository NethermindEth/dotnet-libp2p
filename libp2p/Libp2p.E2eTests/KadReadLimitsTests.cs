// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using Libp2p.Protocols.KadDht;
using Libp2p.Protocols.KadDht.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using NUnit.Framework;

namespace Libp2p.E2eTests;

public class KadReadLimitsTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(7);

    [TestCase("integration", false)]
    [TestCase("integration", true)]
    [TestCase("services", false)]
    [TestCase("services", true)]
    [TestCase("direct", false)]
    [TestCase("direct", true)]
    public async Task RegisteredLimitsRefuseBeforeDispatchAndRecoverAfterReadTimeout(string path, bool customId)
    {
        string id = customId ? "/test/kad/1" : KadDhtOptions.DefaultProtocolId;
        ManualTimeProvider clock = new();
        StreamLogger logger = new(id);
        using MetricRecorder timeouts = new("libp2p.request_response.read_timeouts", id);
        await using var servers = Provider(path, id, clock, logger);
        await using var clients = new ServiceCollection().AddLibp2p().BuildServiceProvider();
        var factory = servers.GetRequiredService<IPeerFactory>();
        await using var serverA = factory.Create();
        await using var serverB = factory.Create();
        await using var first = clients.GetRequiredService<IPeerFactory>().Create();
        await using var second = clients.GetRequiredService<IPeerFactory>().Create();
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(30));
        await ListenAsync(serverA, lifetime.Token);
        await ListenAsync(serverB, lifetime.Token);
        ISession firstToA = await first.DialAsync(serverA.ListenAddresses.ToArray(), lifetime.Token).WaitAsync(TestTimeout);
        ISession firstToB = await first.DialAsync(serverB.ListenAddresses.ToArray(), lifetime.Token).WaitAsync(TestTimeout);
        ISession secondToA = await second.DialAsync(serverA.ListenAddresses.ToArray(), lifetime.Token).WaitAsync(TestTimeout);
        ISession secondToB = await second.DialAsync(serverB.ListenAddresses.ToArray(), lifetime.Token).WaitAsync(TestTimeout);
        var stack = servers.GetRequiredService<IProtocolStackSettings>();
        Assert.That(stack.Protocols!.Keys.Any(p => p.Protocol is NoiseProtocol), Is.True);
        Assert.That(stack.Protocols.Keys.Any(p => p.Protocol is YamuxProtocol), Is.True);
        Assert.That(firstToA.RemotePeerId, Is.EqualTo(serverA.Identity.PeerId));
        var registration = stack.Protocols.Keys.Single(p => p.Id == id);
        Assert.That(registration.AcceptInbound, Is.True);
        var limit = registration.InboundLimit;
        Assert.That(limit, Is.Not.Null);
        Assert.That(limit!.MaxConcurrent, Is.EqualTo(2));
        Assert.That(limit.MaxPerPeer, Is.EqualTo(1));
        Assert.That(((RequestResponseProtocol<Message, Message>)registration.Protocol).ReadTimeout, Is.EqualTo(ReadTimeout));
        await UntilAsync(() => clock.ActiveTimers == 0);
        int timersBefore = clock.DueTimes.Count(t => t == ReadTimeout);
        Task? a = null, b = null;
        try
        {
            WireExchange stallA = new(id, stall: true);
            a = firstToA.DialAsync(stallA, lifetime.Token);
            await stallA.Written.Task.WaitAsync(TestTimeout);
            await UntilAsync(() => limit.InFlight == 1 && logger.ReadStarts == 1);
            await AssertRefusedAsync(firstToB.DialAsync(new WireExchange(id, stall: true), lifetime.Token));
            Assert.That(logger.Refusals, Does.Contain("peer"));
            Assert.That(logger.ReadStarts, Is.EqualTo(1), "Refusal must precede registered listener dispatch.");

            WireExchange stallB = new(id, stall: true);
            b = secondToB.DialAsync(stallB, lifetime.Token);
            await stallB.Written.Task.WaitAsync(TestTimeout);
            await UntilAsync(() => limit.InFlight == 2 && logger.ReadStarts == 2);
            await AssertRefusedAsync(secondToA.DialAsync(new WireExchange(id, stall: true), lifetime.Token));
            Assert.That(logger.Refusals, Does.Contain("total"));
            Assert.That(logger.ReadStarts, Is.EqualTo(2));
            await UntilAsync(() => clock.DueTimes.Count(t => t == ReadTimeout) == timersBefore + 2);
            Assert.That(clock.ActiveTimersFor(ReadTimeout), Is.EqualTo(2));
            Assert.That(timeouts.Measurements, Is.Empty, "Admission refusals are not read timeouts.");
            // The two admitted streams belong to different local peers, proving registration-wide scope.
            clock.Advance(ReadTimeout);
            await AssertRefusedAsync(a);
            await AssertRefusedAsync(b);
            await UntilAsync(() => limit.InFlight == 0 && clock.ActiveTimers == 0);
            Assert.That(logger.Failures.Where(f => f.Direction == "ListenAsync").All(f => f.Level == LogLevel.Debug), Is.True);
            Assert.That(logger.Failures.Count(f => f.Direction == "ListenAsync"), Is.EqualTo(2));
            Assert.That(timeouts.Total("inbound"), Is.EqualTo(2));
            Assert.That(timeouts.Total("outbound"), Is.Zero);

            await firstToA.DialAsync(new WireExchange(id, stall: false), lifetime.Token).WaitAsync(TestTimeout);
            await firstToB.DialAsync(new WireExchange(id, stall: false), lifetime.Token).WaitAsync(TestTimeout);
            Assert.That(firstToA.Closed.IsCancellationRequested, Is.False);
            Assert.That(firstToB.Closed.IsCancellationRequested, Is.False);
            await UntilAsync(() => limit.InFlight == 0);
            Assert.That(timeouts.Total("inbound"), Is.EqualTo(2), "Successful PINGs must not count as timeouts.");
        }
        finally
        {
            lifetime.Cancel();
            await first.DisposeAsync();
            await second.DisposeAsync();
            if (a is not null) await a.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (b is not null) await b.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("integration")]
    [TestCase("services")]
    [TestCase("direct")]
    public async Task RegisteredDialerTimesOutStalledResponseAndKeepsSession(string path)
    {
        string id = KadDhtOptions.DefaultProtocolId;
        ManualTimeProvider clock = new();
        StreamLogger logger = new(id);
        using MetricRecorder timeouts = new("libp2p.request_response.read_timeouts", id);
        await using var clients = Provider(path, id, clock, logger);
        StallingResponder responder = new();
        await using var servers = new ServiceCollection().AddLibp2p(builder => builder.AddProtocol(responder)).BuildServiceProvider();
        await using var server = servers.GetRequiredService<IPeerFactory>().Create();
        await using var client = clients.GetRequiredService<IPeerFactory>().Create();
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(20));
        await ListenAsync(server, lifetime.Token);
        ISession session = await client.DialAsync(server.ListenAddresses.ToArray(), lifetime.Token).WaitAsync(TestTimeout);
        await UntilAsync(() => clock.ActiveTimers == 0);
        Task<Message> dial = session.DialAsync<RequestResponseProtocol<Message, Message>, Message, Message>(
            new Message { Type = Message.Types.MessageType.Ping }, lifetime.Token);
        try
        {
            await responder.Ready.Task.WaitAsync(TestTimeout);
            await UntilAsync(() => clock.ActiveTimers == 1);
            clock.Advance(ReadTimeout);
            var failure = Assert.ThrowsAsync<ChannelClosedException>(async () => await dial.WaitAsync(TestTimeout));
            Assert.That(failure!.Message, Does.Contain(nameof(IOResult.Cancelled)));
            await responder.Completed.Task.WaitAsync(TestTimeout);
            Assert.That(logger.Failures.Single(f => f.Direction == "DialAsync").Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(timeouts.Total("outbound"), Is.EqualTo(1));
            Assert.That(timeouts.Total("inbound"), Is.Zero);
            Assert.That(session.Closed.IsCancellationRequested, Is.False);
            await session.DialAsync<PingProtocol>(lifetime.Token).WaitAsync(TestTimeout);
        }
        finally
        {
            lifetime.Cancel();
            await client.DisposeAsync();
            await server.DisposeAsync();
            await ((Task)dial).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("integration")]
    [TestCase("services")]
    [TestCase("direct")]
    public async Task ClientModeRetainsOutboundDeadlineAndDeniesInbound(string path)
    {
        ManualTimeProvider clock = new();
        await using var provider = Provider(path, "/test/client/kad", clock, new StreamLogger("/test/client/kad"), clientMode: true);
        await using var peer = provider.GetRequiredService<IPeerFactory>().Create();
        var registration = provider.GetRequiredService<IProtocolStackSettings>().Protocols!.Keys.Single(p => p.Id == "/test/client/kad");
        Assert.That(registration.AcceptInbound, Is.False);
        Assert.That(registration.InboundLimit, Is.Not.Null);
        Assert.That(((RequestResponseProtocol<Message, Message>)registration.Protocol).ReadTimeout, Is.EqualTo(ReadTimeout));
    }

    [Test]
    public async Task DirectRegistrationUsesFinalClockAndStreamOptions()
    {
        await using var builder = new Libp2pPeerFactoryBuilder();
        var options = new KadDhtOptions();
        builder.AddKadDhtProtocols(_ => [], options: options);
        ManualTimeProvider clock = new();
        builder.Services.AddSingleton<TimeProvider>(clock);
        options.ReadTimeout = TimeSpan.FromSeconds(9);
        options.MaxInboundStreams = 4;
        options.MaxInboundStreamsPerPeer = 2;
        await using var peer = builder.Build().Create();
        var registration = builder.ServiceProvider.GetRequiredService<IProtocolStackSettings>().Protocols!.Keys
            .Single(p => p.Id == KadDhtOptions.DefaultProtocolId);
        Assert.That(registration.InboundLimit!.MaxConcurrent, Is.EqualTo(4));
        Assert.That(registration.InboundLimit.MaxPerPeer, Is.EqualTo(2));
        var protocol = (RequestResponseProtocol<Message, Message>)registration.Protocol;
        Assert.That(protocol.ReadTimeout, Is.EqualTo(options.ReadTimeout));
        var channel = new Channel();
        var context = NSubstitute.Substitute.For<ISessionContext>();
        Task listener = protocol.ListenAsync(channel, context);
        try
        {
            Assert.That(clock.DueTimes, Is.EqualTo(new[] { options.ReadTimeout }));
            clock.Advance(options.ReadTimeout);
            Assert.ThrowsAsync<ChannelClosedException>(async () => await listener.WaitAsync(TestTimeout));
        }
        finally
        {
            await channel.AbortAsync();
            await listener.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase("integration", nameof(KadDhtOptions.ReadTimeout))]
    [TestCase("services", nameof(KadDhtOptions.ReadTimeout))]
    [TestCase("direct", nameof(KadDhtOptions.ReadTimeout))]
    [TestCase("integration", nameof(KadDhtOptions.MaxInboundStreams))]
    [TestCase("services", nameof(KadDhtOptions.MaxInboundStreams))]
    [TestCase("direct", nameof(KadDhtOptions.MaxInboundStreams))]
    [TestCase("integration", nameof(KadDhtOptions.MaxInboundStreamsPerPeer))]
    [TestCase("services", nameof(KadDhtOptions.MaxInboundStreamsPerPeer))]
    [TestCase("direct", nameof(KadDhtOptions.MaxInboundStreamsPerPeer))]
    public void InvalidOptionsChangedAfterRegistrationAreRejectedAtBuild(string path, string property)
    {
        using var builder = new Libp2pPeerFactoryBuilder();
        var options = new KadDhtOptions();
        switch (path)
        {
            case "integration": builder.AddKadDht(configure: o => options = o); break;
            case "services": builder.Services.AddKadDht(o => options = o); builder.WithKadDht(); break;
            case "direct": builder.AddKadDhtProtocols(_ => [], options: options); break;
        }
        switch (property)
        {
            case nameof(KadDhtOptions.ReadTimeout): options.ReadTimeout = TimeSpan.Zero; break;
            case nameof(KadDhtOptions.MaxInboundStreams): options.MaxInboundStreams = 0; break;
            case nameof(KadDhtOptions.MaxInboundStreamsPerPeer): options.MaxInboundStreamsPerPeer = 0; break;
        }
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => builder.Build());
        Assert.That(failure!.ParamName, Is.EqualTo(property));
        // Failed builds remain configurable and can be retried after correcting the options.
        options.ReadTimeout = TimeSpan.FromSeconds(30);
        options.MaxInboundStreams = 512;
        options.MaxInboundStreamsPerPeer = 64;
        Assert.That(builder.Build(), Is.Not.Null);
    }

    [Test]
    public void DirectRegistrationRejectsNegativeMessageSizeBeforeDeferredBuild()
    {
        using var builder = new Libp2pPeerFactoryBuilder();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddKadDhtProtocols(_ => [],
            options: new KadDhtOptions { MaxMessageSize = -1 }));
    }

    private static ServiceProvider Provider(string path, string id, ManualTimeProvider clock, StreamLogger logger, bool clientMode = false)
    {
        void Configure(KadDhtOptions options)
        {
            options.ProtocolId = id;
            options.ReadTimeout = ReadTimeout;
            options.OperationTimeout = TimeSpan.FromHours(1);
            options.MaxInboundStreams = 2;
            options.MaxInboundStreamsPerPeer = 1;
            options.Mode = clientMode ? KadDhtMode.Client : KadDhtMode.Server;
        }
        var services = new ServiceCollection().AddSingleton<TimeProvider>(clock).AddSingleton<ILoggerFactory>(logger);
        if (path == "services") services.AddKadDht(Configure);
        services.AddLibp2p(builder =>
        {
            if (path == "services") return builder.WithKadDht();
            if (path == "integration") return builder.AddKadDht(Configure);
            var options = new KadDhtOptions();
            Configure(options);
            return builder.AddKadDhtProtocols(_ => [], baseId: id, loggerFactory: logger, options: options);
        });
        return services.BuildServiceProvider();
    }

    private static Task ListenAsync(ILocalPeer peer, CancellationToken token)
        => peer.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TestTimeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task AssertRefusedAsync(Task task)
    {
        Exception? failure = null;
        try { await task.WaitAsync(TestTimeout); }
        catch (Exception ex) { failure = ex; }
        Assert.That(failure, Is.TypeOf<ChannelClosedException>());
    }

    private sealed class WireExchange(string id, bool stall) : ISessionProtocol
    {
        public string Id => id;
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ListenAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            if (stall)
            {
                await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 5 })).OrThrow();
                Written.TrySetResult();
                await channel.ReadAsync(1).OrThrow();
                Assert.Fail("An incomplete request must end in a stream reset.");
            }
            else
            {
                await channel.WriteSizeAndProtobufAsync(new Message { Type = Message.Types.MessageType.Ping });
                var response = await channel.ReadPrefixedProtobufAsync(Message.Parser);
                Assert.That(response.Type, Is.EqualTo(Message.Types.MessageType.Ping));
                Assert.That((await channel.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
            }
        }
    }

    private sealed class StallingResponder : ISessionProtocol
    {
        public string Id => KadDhtOptions.DefaultProtocolId;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DialAsync(IChannel channel, ISessionContext context) => throw new NotSupportedException();
        public async Task ListenAsync(IChannel channel, ISessionContext context)
        {
            try
            {
                await channel.ReadPrefixedProtobufAsync(Message.Parser);
                await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 5 })).OrThrow();
                Ready.TrySetResult();
                await channel.ReadAsync(1).OrThrow();
            }
            finally { Completed.TrySetResult(); }
        }
    }

    private sealed class StreamLogger(string id) : ILoggerFactory, ILogger
    {
        private readonly ConcurrentQueue<KeyValuePair<string, object?>[]> _records = new();
        public ConcurrentQueue<(LogLevel Level, string? Direction)> Failures { get; } = new();
        public int ReadStarts => _records.Count(r => r.Any(p => p.Key == "{OriginalFormat}" &&
            p.Value is string format && format.StartsWith("Starting ListenAsync", StringComparison.Ordinal)));
        public string[] Refusals => _records.SelectMany(r => r.Where(p => p.Key == "Scope").Select(p => (string)p.Value!)).ToArray();
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs) return;
            var record = pairs.ToArray();
            if (!record.Any(p => p.Key == "ProtocolId" && Equals(p.Value, id)) &&
                !record.Any(p => p.Key == "Protocol" && p.Value is ProtocolRef protocol && protocol.Id == id)) return;
            _records.Enqueue(record);
            if (exception is not null) Failures.Enqueue((logLevel, record.FirstOrDefault(p => p.Key == "Direction").Value as string));
        }
    }
}
