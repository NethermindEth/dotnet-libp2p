// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
public class PeerLoggingTests
{
    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    [TestCase(false, 3)]
    [TestCase(true, 3)]
    public async Task FailedUpgradeDistinguishesUnexpectedAndRoutineFailures(bool isListener, int failureKind)
    {
        Exception failure = Failure(failureKind);
        FailingProtocol protocol = new(failure);
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [top] } };
        string message = failureKind == 0 ? "Upgrade task failed" : $"Upgrade task cancelled (closed or duplicate connection): {failure.Message}";
        CapturingLogger logger = new(message);
        using ILoggerFactory factory = logger;
        await using LocalPeer peer = new(TestPeers.Identity(1), null, settings, loggerFactory: factory);
        LocalPeer.Session session = new(peer);

        Assert.ThrowsAsync(failure.GetType(), async () =>
            await peer.Upgrade(session, new Channel(), parent, null, null, isListener, null));

        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(failureKind == 0 ? LogLevel.Warning : LogLevel.Debug));
            Assert.That(entry.Exception?.GetBaseException(), Is.SameAs(failureKind == 0 ? failure : null));
            Assert.That(entry.Message, Is.EqualTo(message));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task FailedInitializationDistinguishesUnexpectedAndRoutineFailures(int failureKind)
    {
        Exception failure = Failure(failureKind);
        CapturingLogger logger = new("Disconnecting due to exception");
        using ILoggerFactory factory = logger;
        await using FailingPeer peer = new(Task.FromException(failure), factory);
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);

        peer.UpgradeToSession(session, new ProtocolRef(new FailingProtocol(failure)), false, null);

        Assert.ThrowsAsync(failure.GetType(), async () => await session.Connected);
        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(failureKind == 0 ? LogLevel.Warning : LogLevel.Debug));
            Assert.That(entry.Exception, Is.SameAs(failure));
            Assert.That(entry.Message, Is.EqualTo("Disconnecting due to exception"));
            Assert.That(session.ConnectionToken.IsCancellationRequested, Is.True);
            Assert.That(peer.Sessions, Is.Empty);
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

        peer.UpgradeToSession(session, new ProtocolRef(new FailingProtocol(Failure(0))), false, null);

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

    private sealed class FailingProtocol(Exception failure) : IConnectionProtocol
    {
        public string Id => "/failing";
        public Task DialAsync(IChannel channel, IConnectionContext context) => Task.FromException(failure);
        public Task ListenAsync(IChannel channel, IConnectionContext context) => Task.FromException(failure);
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
