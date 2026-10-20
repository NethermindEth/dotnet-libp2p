// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.TestsBase;

namespace Nethermind.Libp2p.Core.Tests;

[TestFixture]
public class PeerLoggingTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedUpgradeLogsDebugWithStructuredException(bool isListener)
    {
        InvalidOperationException failure = new("negotiation failed");
        FailingProtocol protocol = new(failure);
        ProtocolRef parent = new(protocol);
        ProtocolRef top = new(protocol);
        ProtocolStackSettings settings = new() { Protocols = new() { [parent] = [top] } };
        CapturingLogger logger = new("Upgrade task failed");
        using ILoggerFactory factory = logger;
        await using LocalPeer peer = new(TestPeers.Identity(1), null, settings, loggerFactory: factory);
        LocalPeer.Session session = new(peer);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await peer.Upgrade(session, new Channel(), parent, null, null, isListener, null));

        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(entry.Exception?.GetBaseException(), Is.SameAs(failure));
            Assert.That(entry.Message, Is.EqualTo("Upgrade task failed"));
        }
    }

    [Test]
    public async Task FailedInitializationLogsDebugWithStructuredException()
    {
        InvalidOperationException failure = new("initialization failed");
        CapturingLogger logger = new("Disconnecting due to exception");
        using ILoggerFactory factory = logger;
        await using FailingPeer peer = new(failure, factory);
        LocalPeer.Session session = new(peer);
        session.State.RemoteAddress = TestPeers.Multiaddr(2);

        peer.UpgradeToSession(session, new ProtocolRef(new FailingProtocol(failure)), false, null);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await session.Connected);
        LogEntry entry = await logger.Entry.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(entry.Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(entry.Exception, Is.SameAs(failure));
            Assert.That(entry.Message, Is.EqualTo("Disconnecting due to exception"));
        }
    }

    private sealed class FailingPeer(Exception failure, ILoggerFactory factory)
        : LocalPeer(TestPeers.Identity(1), null, new ProtocolStackSettings(), loggerFactory: factory)
    {
        protected override Task ConnectedTo(ISession peer, bool isDialer) => Task.FromException(failure);
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
