// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Nethermind.Libp2p.Core.TestsBase;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.Tests;

public class RequestResponseReadTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task OpenStreamReadReceivesCancellationToken(bool dialer)
    {
        var protocol = new RequestResponseProtocol<StringValue, StringValue>("/test/1", (request, _) => Task.FromResult(request));
        Channel wire = new();
        ObservedChannel channel = new(wire);
        Task exchange = dialer
            ? protocol.DialAsync(channel, Context(), new StringValue())
            : protocol.ListenAsync(channel, Context());
        try
        {
            if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
            await channel.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(exchange.IsCompleted, Is.False, "The peer keeps the stream open without sending a frame.");
            Assert.That(channel.ReadToken.CanBeCanceled, Is.True, "The underlying I/O must receive a deadline token.");
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HangupLogsAtDebugAndPreservesException(bool dialer)
    {
        using MetricRecorder metrics = new("libp2p.request_response.read_timeouts", "/test/1");
        RecordingLogger logger = new();
        var protocol = new RequestResponseProtocol<StringValue, StringValue>("/test/1", (request, _) => Task.FromResult(request), logger);
        Channel wire = new();
        ObservedChannel channel = new(wire);
        Task exchange = dialer
            ? protocol.DialAsync(channel, Context(), new StringValue())
            : protocol.ListenAsync(channel, Context());
        try
        {
            if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
            await channel.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await wire.Reverse.WriteEofAsync();
            var failure = Assert.CatchAsync<ChannelClosedException>(async () => await exchange.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(logger.Failures, Has.Count.EqualTo(1));
            Assert.That(logger.Failures[0].Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(logger.Failures[0].Exception, Is.SameAs(failure));
            Assert.That(metrics.Measurements, Is.Empty);
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 1)]
    [TestCase(true, 1)]
    [TestCase(false, 2)]
    [TestCase(true, 2)]
    [TestCase(false, 3)]
    [TestCase(true, 3)]
    public async Task DeadlineCancelsUnderlyingReadAtEveryFrameStage(bool dialer, int stage)
    {
        using MetricRecorder metrics = new("libp2p.request_response.read_timeouts", "/test/1");
        ManualTimeProvider clock = new();
        RecordingLogger logger = new();
        bool handled = false;
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1", (request, _) =>
        {
            handled = true;
            return Task.FromResult(request);
        }, logger, timeProvider: clock);
        Channel wire = new();
        ObservedChannel channel = new(wire);
        Task exchange = dialer ? protocol.DialAsync(channel, Context(), new StringValue()) : protocol.ListenAsync(channel, Context());
        try
        {
            if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
            Assert.That(await channel.NextReadAsync(), Is.EqualTo(1));
            if (stage == 1)
            {
                await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0x80 }));
                Assert.That(await channel.NextReadAsync(), Is.EqualTo(1));
            }
            if (stage >= 2)
            {
                await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 5 }));
                Assert.That(await channel.NextReadAsync(), Is.EqualTo(5));
                if (stage == 3)
                    await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0x0a, 3, (byte)'a' }));
            }
            Assert.That(channel.ReadToken.CanBeCanceled, Is.True);
            Assert.That(channel.TokenAccesses, Is.EqualTo(1));
            Assert.That(clock.DueTimes, Is.EqualTo(new[] { ReadTimeout }));
            Assert.That(exchange.IsCompleted, Is.False);
            clock.Advance(ReadTimeout);
            var failure = Assert.ThrowsAsync<ChannelClosedException>(async () => await exchange.WaitAsync(TestTimeout));
            Assert.That(failure!.Message, Does.Contain(nameof(IOResult.Cancelled)));
            Assert.That(handled, Is.False);
            Assert.That(logger.Failures.Single().Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
            Assert.That(metrics.Measurements, Has.Count.EqualTo(1));
            Assert.That(metrics.Measurements.Single().Value, Is.EqualTo(1));
            Assert.That(metrics.Measurements.Single().Tags, Is.EquivalentTo(new[]
            {
                new KeyValuePair<string, object?>("protocol", "/test/1"),
                new KeyValuePair<string, object?>("direction", dialer ? "outbound" : "inbound")
            }));
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FragmentsShareOneDeadline(bool complete)
    {
        ManualTimeProvider clock = new();
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1",
            (request, _) => Task.FromResult(request), expectsResponse: _ => false, timeProvider: clock);
        Channel wire = new();
        ObservedChannel channel = new(wire);
        Task exchange = protocol.ListenAsync(channel, Context());
        try
        {
            await channel.NextReadAsync();
            byte[] payload = new StringValue { Value = new string('a', 130) }.ToByteArray();
            await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { (byte)(payload.Length | 0x80) }));
            await channel.NextReadAsync();
            clock.Advance(TimeSpan.FromSeconds(4));
            await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }));
            Assert.That(await channel.NextReadAsync(), Is.EqualTo(payload.Length));
            clock.Advance(TimeSpan.FromSeconds(4));
            await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(payload.AsMemory(0, 2)));
            if (complete)
            {
                await wire.Reverse.WriteAsync(new ReadOnlySequence<byte>(payload.AsMemory(2)));
                await exchange.WaitAsync(TestTimeout);
                clock.Advance(ReadTimeout);
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(2));
                Assert.ThrowsAsync<ChannelClosedException>(async () => await exchange.WaitAsync(TestTimeout));
            }
            Assert.That(clock.CreatedTimers, Is.EqualTo(1));
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task ConcurrentReadsHaveIndependentDeadlines()
    {
        ManualTimeProvider clock = new();
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1",
            (request, _) => Task.FromResult(request), timeProvider: clock);
        Channel first = new(), second = new();
        ObservedChannel firstRead = new(first), secondRead = new(second);
        Task a = protocol.ListenAsync(firstRead, Context());
        Task? b = null;
        try
        {
            await firstRead.NextReadAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            b = protocol.ListenAsync(secondRead, Context());
            await secondRead.NextReadAsync();
            Assert.That(firstRead.ReadToken, Is.Not.EqualTo(secondRead.ReadToken));
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.ThrowsAsync<ChannelClosedException>(async () => await a.WaitAsync(TestTimeout));
            Assert.That(secondRead.ReadToken.IsCancellationRequested, Is.False);
            Assert.That(b.IsCompleted, Is.False);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.ThrowsAsync<ChannelClosedException>(async () => await b.WaitAsync(TestTimeout));
            Assert.That(clock.DisposedTimers, Is.EqualTo(2));
        }
        finally
        {
            await first.AbortAsync();
            await second.AbortAsync();
            await a.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (b is not null) await b.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task TimerIsDisposedBeforeHandlerAndHandlerUsesChannelToken()
    {
        ManualTimeProvider clock = new();
        using CancellationTokenSource lifetime = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, async (request, _, token) =>
        {
            handlerToken = token;
            entered.SetResult();
            await release.Task;
            return request;
        }, "/test/1", expectsResponse: _ => false, timeProvider: clock);
        Channel wire = new();
        ObservedChannel channel = new(wire, lifetime.Token);
        Task listener = protocol.ListenAsync(channel, Context());
        try
        {
            await wire.Reverse.WriteSizeAndProtobufAsync(new StringValue());
            await entered.Task.WaitAsync(TestTimeout);
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
            clock.Advance(ReadTimeout);
            Assert.That(handlerToken, Is.EqualTo(lifetime.Token));
            Assert.That(handlerToken.IsCancellationRequested, Is.False);
            release.TrySetResult();
            await listener.WaitAsync(TestTimeout);
        }
        finally
        {
            release.TrySetResult();
            await wire.AbortAsync();
            await listener.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChannelCancellationInterruptsReadBeforeDeadline(bool dialer)
    {
        ManualTimeProvider clock = new();
        RecordingLogger logger = new();
        using CancellationTokenSource lifetime = new();
        Channel wire = new();
        ObservedChannel channel = new(wire, lifetime.Token);
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1",
            (request, _) => Task.FromResult(request), logger, timeProvider: clock);
        Task exchange = dialer ? protocol.DialAsync(channel, Context(), new StringValue()) : protocol.ListenAsync(channel, Context());
        try
        {
            if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
            await channel.NextReadAsync();
            lifetime.Cancel();
            Assert.ThrowsAsync<ChannelClosedException>(async () => await exchange.WaitAsync(TestTimeout));
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
            Assert.That(logger.Failures.Single().Level, Is.EqualTo(LogLevel.Debug));
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public async Task OneWayDialDoesNotStartTimerAndResponseTimerStartsAfterWrite()
    {
        foreach (bool oneWay in new[] { false, true })
        {
            ManualTimeProvider clock = new();
            var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1",
                (request, _) => Task.FromResult(request), expectsResponse: _ => !oneWay, timeProvider: clock);
            Channel wire = new();
            Task exchange = protocol.DialAsync(wire, Context(), new StringValue());
            try
            {
                Assert.That(clock.CreatedTimers, Is.Zero, "No deadline during the blocked request write.");
                await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
                if (oneWay)
                {
                    await exchange.WaitAsync(TestTimeout);
                    Assert.That(clock.CreatedTimers, Is.Zero);
                }
                else
                {
                    await wire.Reverse.WriteSizeAndProtobufAsync(new StringValue());
                    await exchange.WaitAsync(TestTimeout);
                    Assert.That(clock.CreatedTimers, Is.EqualTo(1));
                    Assert.That(clock.DisposedTimers, Is.EqualTo(1));
                }
            }
            finally
            {
                await wire.AbortAsync();
                await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [TestCase(0L)]
    [TestCase(-1L)]
    [TestCase(-10000L)]
    [TestCase(42949672950000L)]
    public void InvalidTimeoutIsRejectedByBothOverloads(long ticks)
    {
        var timeout = TimeSpan.FromTicks(ticks);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestResponseProtocol<StringValue, StringValue>(
            timeout, "/test/1", (request, _) => Task.FromResult(request)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RequestResponseProtocol<StringValue, StringValue>(
            timeout, (request, _, _) => Task.FromResult(request), "/test/1"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TypedFailuresAreClassifiedAndRethrownUnchanged(bool dialer)
    {
        Exception[] failures = [new ChannelClosedException(), new ChannelAbortedException(),
            new SessionExistsException(new Identity().PeerId),
            new AggregateException(new ChannelClosedException(), new AggregateException(new ChannelAbortedException())),
            new AggregateException(new ChannelClosedException(), new InvalidDataException()),
            new AggregateException(), new InvalidDataException(), new OperationCanceledException()];
        foreach (var failure in failures)
        {
            RecordingLogger logger = new();
            var protocol = new RequestResponseProtocol<StringValue, StringValue>("/test/1", (request, _) => Task.FromResult(request), logger);
            Channel wire = new();
            ObservedChannel channel = new(wire) { ReadFailure = failure };
            Task exchange = dialer ? protocol.DialAsync(channel, Context(), new StringValue()) : protocol.ListenAsync(channel, Context());
            try
            {
                if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
                var actual = Assert.CatchAsync<Exception>(async () => await exchange.WaitAsync(TestTimeout));
                Assert.That(actual, Is.SameAs(failure));
                var record = logger.Failures.Single();
                Assert.That(record.Exception, Is.SameAs(failure));
                Assert.That(record.Level, Is.EqualTo(Array.IndexOf(failures, failure) < 4 ? LogLevel.Debug : LogLevel.Warning));
                Assert.That(record.State, Does.Contain(new KeyValuePair<string, object?>("ProtocolId", "/test/1")));
                Assert.That(record.State, Does.Contain(new KeyValuePair<string, object?>("Direction", dialer ? "DialAsync" : "ListenAsync")));
            }
            finally
            {
                await wire.AbortAsync();
                await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task ExpectedCancellationUsesRelevantTokenAndPreservesPropagation(bool dialer, bool deadline)
    {
        using MetricRecorder metrics = new("libp2p.request_response.read_timeouts", "/test/1");
        ManualTimeProvider clock = new();
        RecordingLogger logger = new();
        using CancellationTokenSource lifetime = new();
        Channel wire = new();
        ObservedChannel channel = new(wire, lifetime.Token) { ThrowCancellation = true };
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1",
            (request, _) => Task.FromResult(request), logger, timeProvider: clock);
        Task exchange = dialer ? protocol.DialAsync(channel, Context(), new StringValue()) : protocol.ListenAsync(channel, Context());
        try
        {
            if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
            await channel.NextReadAsync();
            if (deadline) clock.Advance(ReadTimeout);
            else lifetime.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await exchange.WaitAsync(TestTimeout));
            Assert.That(clock.DisposedTimers, Is.EqualTo(1));
            Assert.That(logger.Failures, Has.Count.EqualTo(dialer || deadline ? 1 : 0));
            if (dialer || deadline) Assert.That(logger.Failures.Single().Level, Is.EqualTo(LogLevel.Debug));
            Assert.That(metrics.Total(dialer ? "outbound" : "inbound"), Is.EqualTo(deadline ? 1 : 0));
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailingTimeoutMetricListenerPreservesReadFailure(bool dialer)
    {
        using MetricRecorder metrics = new("libp2p.request_response.read_timeouts", "/test/1",
            () => throw new InvalidOperationException("Telemetry failure"));
        ManualTimeProvider clock = new();
        RecordingLogger logger = new();
        var protocol = new RequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1",
            (request, _) => Task.FromResult(request), logger, timeProvider: clock);
        Channel wire = new();
        ObservedChannel channel = new(wire);
        Task exchange = dialer ? protocol.DialAsync(channel, Context(), new StringValue()) : protocol.ListenAsync(channel, Context());
        try
        {
            if (dialer) await wire.Reverse.ReadPrefixedProtobufAsync(StringValue.Parser);
            await channel.NextReadAsync();
            clock.Advance(ReadTimeout);
            var failure = Assert.ThrowsAsync<ChannelClosedException>(async () => await exchange.WaitAsync(TestTimeout));
            Assert.That(failure!.Message, Does.Contain(nameof(IOResult.Cancelled)));
            Assert.That(metrics.Measurements, Has.Count.EqualTo(1));
            Assert.That(logger.Failures.Single().Exception, Is.SameAs(failure));
            Assert.That(logger.Failures.Single().Level, Is.EqualTo(LogLevel.Debug));
        }
        finally
        {
            await wire.AbortAsync();
            await exchange.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task BuilderOverloadsPropagateTimeoutAndClock(bool cancellationHandler, bool explicitClock)
    {
        ManualTimeProvider clock = new();
        using var provider = new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider();
        var builder = Substitute.For<IPeerFactoryBuilder>();
        builder.ServiceProvider.Returns(provider);
        RequestResponseProtocol<StringValue, StringValue>? protocol = null;
        builder.AddProtocol(Arg.Any<RequestResponseProtocol<StringValue, StringValue>>(), Arg.Any<bool>())
            .Returns(call => { protocol = call.Arg<RequestResponseProtocol<StringValue, StringValue>>(); return builder; });
        if (cancellationHandler)
            builder.AddRequestResponseProtocol<StringValue, StringValue>(ReadTimeout, (request, _, _) => Task.FromResult(request),
                "/test/1", timeProvider: explicitClock ? clock : null);
        else
            builder.AddRequestResponseProtocol<StringValue, StringValue>(ReadTimeout, "/test/1", (request, _) => Task.FromResult(request),
                timeProvider: explicitClock ? clock : null);
        Assert.That(protocol!.ReadTimeout, Is.EqualTo(ReadTimeout));
        Channel wire = new();
        Task listener = protocol.ListenAsync(wire, Context());
        try
        {
            Assert.That(clock.CreatedTimers, Is.EqualTo(1));
            clock.Advance(ReadTimeout);
            Assert.ThrowsAsync<ChannelClosedException>(async () => await listener.WaitAsync(TestTimeout));
        }
        finally
        {
            await wire.AbortAsync();
            await listener.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [Test]
    public void CancellationOverloadsKeepPositionalDefaultCompatibility()
    {
        var protocol = new RequestResponseProtocol<StringValue, StringValue>((request, _, _) => Task.FromResult(request), "/test/1", default);
        Assert.That(protocol.ReadTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
        var builder = Substitute.For<IPeerFactoryBuilder>();
        builder.AddRequestResponseProtocol<StringValue, StringValue>((request, _, _) => Task.FromResult(request), "/test/1", default);
        builder.Received(1).AddProtocol(Arg.Is<RequestResponseProtocol<StringValue, StringValue>>(p => p.Id == "/test/1"), false);
    }

    private static ISessionContext Context()
    {
        var context = Substitute.For<ISessionContext>();
        context.State.Returns(new State());
        return context;
    }

    private sealed class ObservedChannel(IChannel inner, CancellationToken? lifetime = null) : IChannel
    {
        private readonly CancellationToken _lifetime = lifetime ?? inner.CancellationToken;
        private readonly System.Threading.Channels.Channel<int> _reads = System.Threading.Channels.Channel.CreateUnbounded<int>();
        public int TokenAccesses { get; private set; }
        public CancellationToken CancellationToken { get { TokenAccesses++; return _lifetime; } }
        public Exception? ReadFailure { get; init; }
        public bool ThrowCancellation { get; init; }
        private static async ValueTask<ReadResult> WaitForCancellationAsync(CancellationToken token)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The wait must be cancelled.");
        }
        public Task<int> NextReadAsync() => _reads.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReadToken { get; private set; }
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            ReadToken = token;
            ReadStarted.TrySetResult();
            _reads.Writer.TryWrite(length);
            if (ThrowCancellation) return WaitForCancellationAsync(token);
            return ReadFailure is { } failure ? ValueTask.FromException<ReadResult>(failure) : inner.ReadAsync(length, blockingMode, token);
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }

    private sealed class RecordingLogger : ILoggerFactory, ILogger
    {
        public List<(LogLevel Level, Exception Exception, KeyValuePair<string, object?>[] State)> Failures { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Failures.Add((logLevel, exception, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToArray()));
        }
    }
}
