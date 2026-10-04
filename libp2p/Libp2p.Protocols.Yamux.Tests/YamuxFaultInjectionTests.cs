// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

/// <summary>
/// Fault-injection tests for <see cref="YamuxProtocol"/> control paths:
/// failing transports and upchannels, blocked control writes, redundant
/// session upgrades, and small uncovered API-surface gaps. Deterministic;
/// no new dependencies.
/// </summary>
[TestFixture]
public class YamuxFaultInjectionTests
{
    [Test]
    public void ProtocolId_IsYamux()
    {
        Assert.That(new YamuxProtocol().Id, Is.EqualTo("/yamux/1.0.0"));
    }

    [Test]
    public void Ctor_RegistersWithMultiplexerSettings()
    {
        MultiplexerSettings settings = new();
        YamuxProtocol protocol = new(multiplexerSettings: settings);
        Assert.That(settings.Multiplexers, Does.Contain(protocol));
    }

    [Test]
    public void AbortOutbound_AfterDispose_DoesNotThrow()
    {
        ChannelState state = new(null, new YamuxWindowSettings());
        state.Dispose();
        Assert.DoesNotThrow(() => state.AbortOutbound());
    }

    [Test]
    public async Task TransportWriteFailure_OutboundPumpClosesStreamWithoutFault()
    {
        // Every transport write throws ChannelClosedException while the read loop
        // stays parked: the outbound pump must take the non-cancelled
        // ChannelClosedException path and the session must still terminate cleanly.
        (IConnectionContext ctx, _) = Mocks.Dialer(
            [new UpgradeOptions { SelectedProtocol = Mocks.TestProtocol() }]);
        FaultTransport transport = new(failWritesWith: _ => new ChannelClosedException(IOResult.Ended));
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            // Give the pump a chance to hit the failing SYN write.
            await Task.Delay(500, timeout.Token);
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False,
                $"Unhandled exception escaped: {yamux.Exception?.GetBaseException().Message}");
        }
        finally
        {
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task AppReadFailure_PumpSendsResetAndSessionSurvives()
    {
        // The upchannel read fails with a non-channel exception: the pump must
        // reset the stream and the session must stay alive.
        (IConnectionContext ctx, INewSessionContext session) = Mocks.Listener();
        FaultAppChannel app = new(failReadsWith: _ => new InvalidOperationException("app read blew up"));
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(app);
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Syn,
                StreamID = 2
            }, timeout.Token);
            YamuxHeader? reset = await WaitForFlagAsync(outbound, YamuxHeaderFlags.Rst, streamId: 2, timeout.Token);
            Assert.That(reset, Is.Not.Null, "Pump failure must reset the stream.");
            await WriteHeaderAsync(remote, FuzzPingSyn(21), timeout.Token);
            YamuxHeader? ack = await WaitForPingAckAsync(outbound, 21, timeout.Token);
            Assert.That(ack, Is.Not.Null, "Session must survive an upchannel failure.");
            Assert.That(FirstGoAway(outbound), Is.Null);
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    [Test]
    public async Task AppWriteFailure_DoesNotKillSession()
    {
        // The fire-and-forget upchannel write faults: the continuation must
        // swallow it (skipping the window extension) without touching the session.
        (IConnectionContext ctx, INewSessionContext session) = Mocks.Listener();
        FaultAppChannel app = new(failWritesWith: _ => new InvalidOperationException("app write blew up"));
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(app);
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Syn,
                StreamID = 2
            }, timeout.Token);
            byte[] payload = [9, 8, 7, 6, 5, 4, 3, 2];
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.Data,
                StreamID = 2,
                Length = payload.Length
            }, timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            await WriteHeaderAsync(remote, FuzzPingSyn(22), timeout.Token);
            YamuxHeader? ack = await WaitForPingAckAsync(outbound, 22, timeout.Token);
            Assert.That(ack, Is.Not.Null, "Session must survive a faulted upchannel write.");
            Assert.That(FirstGoAway(outbound), Is.Null);
            await Task.Delay(300, timeout.Token);
            Assert.That(yamux.IsFaulted, Is.False);
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    [Test]
    public async Task TransportReadFailure_SendsInternalErrorGoAway()
    {
        // The transport read fails with a non-channel exception: the outer
        // catch-all must answer GoAway/InternalError and close without faulting.
        (IConnectionContext ctx, _) = Mocks.Dialer([]);
        FaultTransport transport = new(failReadsWith: _ => new InvalidOperationException("transport read blew up"));
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        // Drain the real channel: the stub's reads always throw.
        Task drain = DrainAsync(transport.Inner.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        try
        {
            YamuxHeader? goAway = await WaitForGoAwayAsync(outbound, TimeSpan.FromSeconds(10));
            Assert.That(goAway, Is.Not.Null, "Transport failure must emit a GoAway.");
            Assert.That(goAway!.Value.Length, Is.EqualTo((int)SessionTerminationCode.InternalError));
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        finally
        {
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await drain;
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TransportReadAndWriteFailure_SessionTerminatesWithoutFault(bool channelClosed)
    {
        // Both directions are dead: the farewell GoAway write itself fails, and
        // that failure must be swallowed instead of faulting the session task.
        (IConnectionContext ctx, _) = Mocks.Dialer([]);
        FaultTransport transport = new(
            failReadsWith: _ => new InvalidOperationException("transport read blew up"),
            failWritesWith: _ => channelClosed
                ? new ChannelClosedException(IOResult.Ended)
                : new IOException("transport write blew up"));
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        try
        {
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False,
                $"Dead transport must not fault the session: {yamux.Exception?.GetBaseException().Message}");
        }
        finally
        {
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestCase(0)] // bad version
    [TestCase(1)] // unknown type
    [TestCase(2)] // negative data length
    public async Task BlockedGoAway_TerminatesOnTimeout(int violationKind)
    {
        (IConnectionContext ctx, _) = Mocks.Dialer([]);
        ManualClock clock = new();
        GatedTransport transport = new(blockedWriteIndex: 1);
        Task yamux = new YamuxProtocol(timeProvider: clock).DialAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader bad = violationKind switch
            {
                0 => new YamuxHeader { Version = 1, Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn },
                1 => new YamuxHeader { Type = (YamuxHeaderType)99, StreamID = 2 },
                _ => new YamuxHeader { Type = YamuxHeaderType.Data, StreamID = 2, Length = -1 },
            };
            await WriteHeaderAsync(remote, bad, timeout.Token);
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);
            (TimerCallback callback, object? state, TimeSpan dueTime) =
                await clock.NextTimerAsync(timeout.Token);
            Assert.That(dueTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
            clock.Advance(TimeSpan.FromSeconds(10));
            callback(state);
            await yamux.WaitAsync(timeout.Token);
            Assert.That(transport.CancelledWriteCount, Is.EqualTo(1));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        finally
        {
            transport.Release();
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task FullStreamTable_BlockedResetDoesNotStallSession()
    {
        (IConnectionContext ctx, _) = Mocks.Listener();
        ManualClock clock = new();
        GatedTransport transport = new(blockedWriteIndex: 513);
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol(timeProvider: clock).ListenAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        IChannel remote = transport.Reverse();
        Task drain = DrainAsync(remote, outbound, timeout.Token);
        try
        {
            byte[] opens = new byte[512 * 12];
            for (int i = 0; i < 512; i++)
            {
                YamuxHeader syn = new() { Type = YamuxHeaderType.WindowUpdate, Flags = YamuxHeaderFlags.Syn, StreamID = 1 + 2 * i };
                YamuxHeader.ToBytes(opens.AsSpan(i * 12, 12), ref syn);
            }
            await remote.WriteAsync(new ReadOnlySequence<byte>(opens), timeout.Token).OrThrow();
            for (int i = 0; i < 400; i++)
            {
                lock (outbound)
                {
                    if (outbound.Count(o => o.Header.Type == YamuxHeaderType.WindowUpdate &&
                        (o.Header.Flags & YamuxHeaderFlags.Ack) != 0) == 512)
                        break;
                }
                await Task.Delay(25, timeout.Token);
            }
            lock (outbound)
                Assert.That(outbound.Count(o => (o.Header.Flags & YamuxHeaderFlags.Ack) != 0), Is.EqualTo(512));

            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Syn,
                StreamID = 1025
            }, timeout.Token);
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);
            (TimerCallback callback, object? state, TimeSpan dueTime) = await clock.NextTimerAsync(timeout.Token);
            Assert.That(dueTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
            clock.Advance(dueTime);
            callback(state);

            await WriteHeaderAsync(remote, FuzzPingSyn(46), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 46, timeout.Token), Is.Not.Null);
            Assert.That(transport.CancelledWriteCount, Is.EqualTo(1));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        finally
        {
            transport.Release();
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            await drain;
        }
    }

    [Test]
    public async Task RedundantSessionUpgrade_ReturnsCleanly()
    {
        // A second listener on an already-upgraded context hits
        // SessionExistsException and must return without affecting the first.
        IProtocol protocol = Mocks.TestProtocol();
        IConnectionContext context = Substitute.For<IConnectionContext>();
        INewSessionContext session = Substitute.For<INewSessionContext>();
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        session.Id.Returns("listener");
        session.SubProtocols.Returns([protocol]);
        List<TestChannel> ups = [];
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            TestChannel up = new();
            lock (ups) { ups.Add(up); }
            return up;
        });
        int calls = 0;
        context.UpgradeToSession().Returns(_ => Interlocked.Increment(ref calls) == 1
            ? session
            : throw new SessionExistsException(TestPeers.PeerId(2)));

        TestChannel transportA = new();
        TestChannel transportB = new();
        YamuxProtocol yamux = new();
        Task listenA = yamux.ListenAsync(transportA, context);
        Task listenB = yamux.ListenAsync(transportB, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remoteA = transportA.Reverse();
            await WriteHeaderAsync(remoteA, new YamuxHeader
            {
                Type = YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Syn,
                StreamID = 1
            }, timeout.Token);
            // Wait until A owns the session before B attempts its upgrade.
            for (int i = 0; i < 200 && ups.Count == 0; i++)
            {
                await Task.Delay(25, timeout.Token);
            }
            Assert.That(ups, Is.Not.Empty, "First listener must own the session.");
            IChannel remoteB = transportB.Reverse();
            await WriteHeaderAsync(remoteB, new YamuxHeader
            {
                Type = YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Syn,
                StreamID = 1
            }, timeout.Token);
            // B must return promptly via the redundant-session path.
            await listenB.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(listenB.IsFaulted, Is.False);
            // A keeps working.
            await WriteHeaderAsync(remoteA, FuzzPingSyn(23), timeout.Token);
            List<FuzzObservation> outbound = [];
            Task drain = DrainAsync(remoteA, outbound, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            YamuxHeader? ack = await WaitForPingAckAsync(outbound, 23, timeout.Token);
            Assert.That(ack, Is.Not.Null);
            await transportA.CloseAsync();
            await drain;
        }
        finally
        {
            await transportA.CloseAsync();
            await transportB.CloseAsync();
            await Task.WhenAll(listenA, listenB).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    internal sealed record FuzzObservation(YamuxHeader Header, byte[] Payload);

    internal static YamuxHeader FuzzPingSyn(int length) => new()
    {
        Type = YamuxHeaderType.Ping,
        Flags = YamuxHeaderFlags.Syn,
        StreamID = 0,
        Length = length
    };

    internal static async Task WriteHeaderAsync(IChannel remote, YamuxHeader header, CancellationToken token)
    {
        byte[] frame = new byte[12];
        YamuxHeader copy = header;
        YamuxHeader.ToBytes(frame, ref copy);
        Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(frame), token), Is.EqualTo(IOResult.Ok));
    }

    internal static async Task DrainAsync(IChannel remote, List<FuzzObservation> outbound, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                ReadResult headerRead = await remote.ReadAsync(12, token: token);
                if (headerRead.Result != IOResult.Ok)
                {
                    break;
                }
                YamuxHeader header = YamuxHeader.FromBytes(headerRead.Data.ToArray());
                byte[] payload = [];
                if (header.Type == YamuxHeaderType.Data && header.Length > 0 && header.Length <= 64 * 1024 * 1024)
                {
                    ReadResult payloadRead = await remote.ReadAsync(header.Length, token: token);
                    if (payloadRead.Result == IOResult.Ok)
                    {
                        payload = payloadRead.Data.ToArray();
                    }
                }
                lock (outbound) { outbound.Add(new FuzzObservation(header, payload)); }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal static FuzzObservation? FirstGoAway(List<FuzzObservation> outbound)
    {
        lock (outbound) { return outbound.FirstOrDefault(o => o.Header.Type == YamuxHeaderType.GoAway); }
    }

    internal static async Task<YamuxHeader?> WaitForGoAwayAsync(List<FuzzObservation> outbound, TimeSpan budget)
    {
        using CancellationTokenSource timeout = new(budget);
        for (int i = 0; i < 200; i++)
        {
            FuzzObservation? goAway = FirstGoAway(outbound);
            if (goAway is not null)
            {
                return goAway.Header;
            }
            await Task.Delay(25, timeout.Token);
        }
        return FirstGoAway(outbound)?.Header;
    }

    internal static async Task<YamuxHeader?> WaitForPingAckAsync(
        List<FuzzObservation> outbound, int opaque, CancellationToken token)
    {
        for (int i = 0; i < 200; i++)
        {
            lock (outbound)
            {
                FuzzObservation? ack = outbound.FirstOrDefault(o =>
                    o.Header.Type == YamuxHeaderType.Ping &&
                    (o.Header.Flags & YamuxHeaderFlags.Ack) != 0 &&
                    o.Header.Length == opaque);
                if (ack is not null)
                {
                    return ack.Header;
                }
            }
            await Task.Delay(25, token);
        }
        return null;
    }

    internal static async Task<YamuxHeader?> WaitForFlagAsync(
        List<FuzzObservation> outbound, YamuxHeaderFlags flag, int streamId, CancellationToken token)
    {
        for (int i = 0; i < 200; i++)
        {
            lock (outbound)
            {
                FuzzObservation? found = outbound.FirstOrDefault(o =>
                    o.Header.StreamID == streamId && (o.Header.Flags & flag) != 0);
                if (found is not null)
                {
                    return found.Header;
                }
            }
            await Task.Delay(25, token);
        }
        return null;
    }

    /// <summary>Minimal mocked contexts.</summary>
    internal static class Mocks
    {
        public static IProtocol TestProtocol()
        {
            IProtocol protocol = Substitute.For<IProtocol>();
            protocol.Id.Returns("/test/1.0.0");
            return protocol;
        }

        public static (IConnectionContext Ctx, INewSessionContext Session) Dialer(IEnumerable<UpgradeOptions> dialRequests)
        {
            IProtocol protocol = TestProtocol();
            IConnectionContext context = Substitute.For<IConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            context.UpgradeToSession().Returns(session);
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
            session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
            session.Id.Returns("fuzz-dialer");
            session.DialRequests.Returns(dialRequests);
            session.SubProtocols.Returns([protocol]);
            session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ => new TestChannel());
            return (context, session);
        }

        public static (IConnectionContext Ctx, INewSessionContext Session) Listener()
        {
            IProtocol protocol = TestProtocol();
            IConnectionContext context = Substitute.For<IConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            context.UpgradeToSession().Returns(session);
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
            session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
            session.Id.Returns("fuzz-listener");
            session.SubProtocols.Returns([protocol]);
            session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ => new TestChannel());
            return (context, session);
        }
    }

    /// <summary>Upchannel stub whose reads/writes fail with configured exceptions (async: faulted tasks).</summary>
    internal sealed class FaultAppChannel(
        Func<int, Exception?>? failReadsWith = null,
        Func<int, Exception?>? failWritesWith = null) : IChannel
    {
        private readonly TestChannel _inner = new();
        private int _reads;
        private int _writes;

        public IChannel Reverse() => _inner.Reverse();

        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (failReadsWith?.Invoke(Interlocked.Increment(ref _reads)) is { } readFailure)
            {
                throw readFailure;
            }
            return await _inner.ReadAsync(length, blockingMode, token);
        }

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            if (failWritesWith?.Invoke(Interlocked.Increment(ref _writes)) is { } writeFailure)
            {
                throw writeFailure;
            }
            return await _inner.WriteAsync(bytes, token);
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => _inner.WriteEofAsync(token);
        public TaskAwaiter GetAwaiter() => _inner.GetAwaiter();
        public ValueTask CloseAsync() => _inner.CloseAsync();
        public ValueTask AbortAsync() => _inner.AbortAsync();
    }

    /// <summary>Transport stub whose reads/writes fail with configured exceptions (async: faulted tasks).</summary>
    internal sealed class FaultTransport(
        Func<int, Exception?>? failReadsWith = null,
        Func<int, Exception?>? failWritesWith = null) : IChannel
    {
        public TestChannel Inner { get; } = new();
        private int _reads;
        private int _writes;

        public IChannel Reverse() => Inner.Reverse();

        public async ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (failReadsWith?.Invoke(Interlocked.Increment(ref _reads)) is { } readFailure)
            {
                throw readFailure;
            }
            return await Inner.ReadAsync(length, blockingMode, token);
        }

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            if (failWritesWith?.Invoke(Interlocked.Increment(ref _writes)) is { } writeFailure)
            {
                throw writeFailure;
            }
            return await Inner.WriteAsync(bytes, token);
        }

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Inner.WriteEofAsync(token);
        public TaskAwaiter GetAwaiter() => Inner.GetAwaiter();
        public ValueTask CloseAsync() => Inner.CloseAsync();
        public ValueTask AbortAsync() => Inner.AbortAsync();
    }

    /// <summary>Transport that blocks one write until released; counts cancelled writes.</summary>
    internal sealed class GatedTransport(int blockedWriteIndex) : IChannel
    {
        public TestChannel Inner { get; } = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;
        private int _cancelled;

        public TaskCompletionSource WriteBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CancelledWriteCount => Volatile.Read(ref _cancelled);

        public IChannel Reverse() => Inner.Reverse();

        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref _writes) != blockedWriteIndex)
            {
                return await Inner.WriteAsync(bytes, token);
            }
            WriteBlocked.TrySetResult();
            try
            {
                await _release.Task.WaitAsync(token);
                return await Inner.WriteAsync(bytes, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                return IOResult.Cancelled;
            }
        }

        public ValueTask<ReadResult> ReadAsync(int length,
            ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default) =>
            Inner.ReadAsync(length, blockingMode, token);

        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Inner.WriteEofAsync(token);
        public TaskAwaiter GetAwaiter() => Inner.GetAwaiter();
        public ValueTask CloseAsync() => Inner.CloseAsync();
        public ValueTask AbortAsync() => Inner.AbortAsync();

        public void Release() => _release.TrySetResult();
    }

    /// <summary>Manually advanced clock for timeout-driven control paths.</summary>
    internal sealed class ManualClock : TimeProvider
    {
        private long _now;
        private readonly ConcurrentQueue<(TimerCallback Callback, object? State, TimeSpan DueTime)> _timers = new();
        private readonly SemaphoreSlim _timerArrived = new(0);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _now);
        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timers.Enqueue((callback, state, dueTime));
            _timerArrived.Release();
            return Substitute.For<ITimer>();
        }

        public async Task<(TimerCallback Callback, object? State, TimeSpan DueTime)> NextTimerAsync(CancellationToken token)
        {
            await _timerArrived.WaitAsync(token);
            _timers.TryDequeue(out (TimerCallback Callback, object? State, TimeSpan DueTime) timer);
            return timer;
        }
    }
}
