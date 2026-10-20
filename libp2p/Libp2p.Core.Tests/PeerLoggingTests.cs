// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
public class PeerLoggingTests
{
    [TestCase(false, 0, false)]
    [TestCase(true, 0, false)]
    [TestCase(false, 1, true)]
    [TestCase(true, 1, true)]
    [TestCase(false, 2, true)]
    [TestCase(true, 2, true)]
    [TestCase(false, 3, true)]
    [TestCase(true, 3, true)]
    [TestCase(false, 4, true)]
    [TestCase(true, 4, true)]
    [TestCase(false, 5, true)]
    [TestCase(true, 5, true)]
    [TestCase(false, 6, false)]
    [TestCase(true, 6, false)]
    [TestCase(false, 7, false)]
    [TestCase(true, 7, false)]
    public async Task FailedUpgradeDistinguishesUnexpectedAndRoutineFailures(bool isListener, int failureKind, bool routine)
    {
        Exception failure = Failure(failureKind);
        FailingProtocol protocol = new(Task.FromException(failure));
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [top] } };
        string message = routine ? $"Upgrade task cancelled (closed or duplicate connection): {failure.Message}" : "Upgrade task failed";
        CapturingLogger logger = new("Upgrade task");
        using ILoggerFactory factory = logger;
        await using LocalPeer peer = new(TestPeers.Identity(1), null, settings, loggerFactory: factory);
        LocalPeer.Session session = new(peer);

        Assert.ThrowsAsync(failure.GetType(), async () =>
            await peer.Upgrade(session, new Channel(), parent, null, null, isListener, null));

        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(routine ? LogLevel.Debug : LogLevel.Warning));
            Assert.That(entry.Exception?.GetBaseException(), Is.SameAs(routine ? null : failure));
            Assert.That(entry.Message, Is.EqualTo(message));
        }
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(2, true)]
    [TestCase(3, true)]
    [TestCase(4, true)]
    [TestCase(5, true)]
    [TestCase(6, false)]
    [TestCase(7, false)]
    public async Task FailedInitializationDistinguishesUnexpectedAndRoutineFailures(int failureKind, bool routine)
    {
        Exception failure = Failure(failureKind);
        CapturingLogger logger = new("Disconnecting due to exception");
        using ILoggerFactory factory = logger;
        await using FailingPeer peer = new(Task.FromException(failure), factory);
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);

        peer.UpgradeToSession(session, new ProtocolRef(new FailingProtocol(Task.FromException(failure))), false, null);

        Assert.ThrowsAsync(failure.GetType(), async () => await session.Connected);
        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(routine ? LogLevel.Debug : LogLevel.Warning));
            Assert.That(entry.Exception, Is.SameAs(failure));
            Assert.That(entry.Message, Is.EqualTo("Disconnecting due to exception"));
            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.Empty);
        }
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    public async Task UpgradeClassifiesEveryFaultFromWhenAll(bool isListener, bool mixed, bool unexpectedFirst)
    {
        Exception routine = new ChannelClosedException();
        Exception other = mixed ? new InvalidOperationException("application fault") : new ChannelAbortedException();
        Exception[] failures = unexpectedFirst ? [other, routine] : [routine, other];
        Task completion = Task.WhenAll(failures.Select(Task.FromException));
        FailingProtocol protocol = new(completion);
        ProtocolRef parent = new(protocol);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [new(protocol)] } };
        CapturingLogger logger = new("Upgrade task");
        using ILoggerFactory factory = logger;
        await using LocalPeer peer = new(TestPeers.Identity(1), null, settings, loggerFactory: factory);
        LocalPeer.Session session = new(peer);

        Assert.That(Assert.CatchAsync<Exception>(async () =>
            await peer.Upgrade(session, new Channel(), parent, null, null, isListener, null)), Is.SameAs(failures[0]));

        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(entry.Level, Is.EqualTo(mixed ? LogLevel.Warning : LogLevel.Debug));
        if (mixed)
        {
            Assert.That(entry.Exception, Is.InstanceOf<AggregateException>());
            Assert.That(((AggregateException)entry.Exception!).InnerExceptions, Is.EqualTo(failures));
        }
        else
        {
            Assert.That(entry.Exception, Is.Null);
        }
    }

    [Test]
    public async Task CanceledInitializationDoesNotLogFailure()
    {
        CapturingLogger logger = new("Disconnecting due to exception");
        using ILoggerFactory factory = logger;
        await using FailingPeer peer = new(Task.FromCanceled(new CancellationToken(true)), factory);
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);

        peer.UpgradeToSession(session, new ProtocolRef(new FailingProtocol(Task.FromException(Failure(0)))), false, null);

        Assert.CatchAsync<OperationCanceledException>(async () => await session.Connected);
        Assert.That(logger.Entry.Task.IsCompleted, Is.False);
        Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
        Assert.That(peer.Sessions, Is.Empty);
    }

    [Test]
    public async Task ResetInitializationStreamLogsDebugBeforeClosingSession()
    {
        ReadingProtocol protocol = new();
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [top] } };
        CapturingLogger logger = new("Disconnecting due to exception");
        using ILoggerFactory factory = logger;
        await using DialingPeer peer = new(settings, factory);
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);
        TaskCompletionSource removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Sessions.CollectionChanged += (_, _) =>
        {
            if (peer.Sessions.Count == 0) removed.TrySetResult();
        };
        using INewSessionContext context = peer.UpgradeToSession(session, parent, false, null);
        UpgradeOptions request = session.GetRequestQueue().First();
        Assert.That(request.SelectedProtocol, Is.SameAs(protocol));
        Channel channel = new();
        Task upgrade = peer.Upgrade(session, channel, parent, protocol, request, false, null);
        Assert.That(upgrade.IsCompleted, Is.False);
        Assert.That(session.ConnectionToken.IsCancellationRequested, Is.False, "Only the initialization stream is being reset.");

        await channel.AbortAsync();

        ChannelClosedException failure = Assert.ThrowsAsync<ChannelClosedException>(async () => await upgrade.WaitAsync(TimeSpan.FromSeconds(2)))!;
        Assert.That(Assert.ThrowsAsync<ChannelClosedException>(async () => await session.Connected.WaitAsync(TimeSpan.FromSeconds(2))), Is.SameAs(failure));
        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await removed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(entry.Exception, Is.SameAs(failure));
            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.Empty);
        }
    }

    private static Exception Failure(int failureKind) => failureKind switch
    {
        1 => new ChannelClosedException(),
        2 => new ChannelAbortedException(),
        3 => new SessionExistsException(TestPeers.Identity(2).PeerId),
        4 => new AggregateException(new ChannelClosedException(), new ChannelAbortedException()),
        5 => new AggregateException(new AggregateException(new ChannelClosedException(), new SessionExistsException(TestPeers.PeerId(2))), new ChannelAbortedException()),
        6 => new AggregateException(new AggregateException(new ChannelClosedException()), new InvalidOperationException("application fault")),
        7 => new AggregateException(),
        _ => new InvalidOperationException("negotiation failed")
    };

    private sealed class FailingPeer(Task initialization, ILoggerFactory factory)
        : LocalPeer(TestPeers.Identity(1), null, new ProtocolStackSettings(), loggerFactory: factory)
    {
        protected override Task ConnectedTo(ISession peer, bool isDialer) => initialization;
    }

    private sealed class DialingPeer(IProtocolStackSettings settings, ILoggerFactory factory)
        : LocalPeer(TestPeers.Identity(1), null, settings, loggerFactory: factory)
    {
        protected override Task ConnectedTo(ISession peer, bool isDialer) => peer.DialAsync<ReadingProtocol>();
    }

    private sealed class ReadingProtocol : ISessionProtocol
    {
        public string Id => "/reading";
        public async Task DialAsync(IChannel channel, ISessionContext context)
        {
            await channel.ReadPrefixedProtobufAsync(Google.Protobuf.WellKnownTypes.Empty.Parser);
        }
        public Task ListenAsync(IChannel channel, ISessionContext context) => DialAsync(channel, context);
    }

    private sealed class FailingProtocol(Task failure) : IConnectionProtocol
    {
        public string Id => "/failing";
        public Task DialAsync(IChannel channel, IConnectionContext context) => failure;
        public Task ListenAsync(IChannel channel, IConnectionContext context) => failure;
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class CapturingLogger(string prefix) : ILogger, ILoggerFactory
    {
        public TaskCompletionSource<LogEntry> Entry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (message.StartsWith(prefix)) Entry.TrySetResult(new(level, exception, message));
        }
    }
}
