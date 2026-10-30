// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using static Nethermind.Libp2p.Protocols.Yamux.Tests.YamuxFaultInjectionTests;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

[TestFixture]
public class YamuxAdmissionTests
{
    [TestCase(262144)]
    [TestCase(524288)]
    public void ReceiveCapacityDoesNotGrantSendCredit(int receiveWindow)
    {
        using ChannelState state = new(null, new YamuxWindowSettings { InitialWindowSize = receiveWindow });
        Assert.Multiple(() =>
        {
            Assert.That(state.LocalWindow.Available, Is.EqualTo(receiveWindow));
            Assert.That(state.RemoteWindow.Available, Is.EqualTo(YamuxProtocol.ProtocolInitialWindowSize));
        });
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task NormalGoAwayKeepsBufferedAndLaterTraffic(bool listener, bool finBeforeLoss)
    {
        await using Harness h = new(listener);
        int id = h.InboundId;
        await h.OpenInbound(id);
        IChannel app = h.Apps[0].Reverse();
        await h.Send(Data(id, 3), [1, 2, 3]);
        await h.Send(GoAway());
        await h.Send(GoAway());
        await h.Ping(); // Serial receive-loop barrier after both GoAway frames.
        Assert.That(h.Run.IsCompleted, Is.False);
        Assert.That((await app.ReadAsync(3, token: h.Token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        await h.Send(Data(id, 2), [4, 5]);
        Assert.That((await app.ReadAsync(2, token: h.Token).OrThrow()).ToArray(), Is.EqualTo(new byte[] { 4, 5 }));
        Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 6, 7 }), h.Token), Is.EqualTo(IOResult.Ok));
        var reply = await h.Next();
        Assert.That(reply.Header.Type, Is.EqualTo(YamuxHeaderType.Data));
        Assert.That(reply.Payload, Is.EqualTo(new byte[] { 6, 7 }));
        await h.Send(Data(id, 1), [8]);
        if (finBeforeLoss) await h.Send(Update(id, YamuxHeaderFlags.Fin));
        await h.Ping();
        await h.Transport.CloseAsync();
        await h.Run.WaitAsync(h.Token);
        ReadResult final = await app.ReadAsync(1, token: h.Token);
        if (finBeforeLoss)
        {
            Assert.That(final.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(final.Data.ToArray(), Is.EqualTo(new byte[] { 8 }));
            final = await app.ReadAsync(1, token: h.Token);
        }
        Assert.That(final.Result, Is.EqualTo(finBeforeLoss ? IOResult.Ended : IOResult.Aborted));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task GoAwayRejectsNewStreamsAndCompletesDialRequests(bool listener, bool dataSyn)
    {
        await using Harness h = new(listener);
        if (listener) await h.OpenInbound(h.InboundId);
        int before = h.Apps.Count;
        await h.Send(GoAway());
        await h.Ping();
        await h.Send(dataSyn ? Data(h.InboundId + 2, 3, YamuxHeaderFlags.Syn) : Update(h.InboundId + 2, YamuxHeaderFlags.Syn),
            dataSyn ? [1, 2, 3] : []);
        Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
        await h.Ping();
        for (int i = 0; i < 2; i++)
        {
            TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Requests.Add(new UpgradeOptions { CompletionSource = completion });
            Assert.ThrowsAsync<ChannelClosedException>(async () => await completion.Task.WaitAsync(h.Token));
        }
        Assert.That(h.Apps, Has.Count.EqualTo(before));
    }

    [Test]
    public async Task GoAwayBeforeFirstListenerStreamDoesNotCreateSession()
    {
        await using Harness h = new(true);
        await h.Send(GoAway());
        await h.Send(Data(1, 3, YamuxHeaderFlags.Syn), [1, 2, 3]);
        Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
        await h.Ping();
        h.Context.DidNotReceive().UpgradeToSession();
        Assert.That(h.Apps, Is.Empty);
    }

    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    [TestCase(true, true, true)]
    [TestCase(false, false, false, true)]
    [TestCase(true, true, false, true)]
    [TestCase(false, true, true, true)]
    [TestCase(true, false, true, true)]
    public async Task DuplicateSynIsFatalBeforePayloadOrCredit(bool listener, bool dataSyn, bool graceful, bool full = false)
    {
        await using Harness h = new(listener);
        await h.OpenInbound(h.InboundId);
        if (full)
            for (int i = 1; i < 512; i++) await h.OpenInbound(h.InboundId + i * 2);
        if (graceful) { await h.Send(GoAway()); await h.Ping(); }
        // No duplicate DATA body is provided. A duplicate must fail before waiting for it.
        await h.SendFinal(dataSyn ? Data(h.InboundId, 123, YamuxHeaderFlags.Syn) : Update(h.InboundId, YamuxHeaderFlags.Syn, 123));
        YamuxHeader error = (await h.Next()).Header;
        Assert.That(error.Type, Is.EqualTo(YamuxHeaderType.GoAway));
        Assert.That(error.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await h.Run.WaitAsync(h.Token);
        Assert.That(h.Apps, Has.Count.EqualTo(full ? 512 : 1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task LocalStreamsDoNotConsumeInboundCapacity(bool listener)
    {
        await using Harness h = new(listener);
        if (listener) await h.OpenInbound(h.InboundId);
        for (int i = 0; i < 512; i++) h.Requests.Add(new UpgradeOptions());
        for (int i = 0; i < 512; i++) Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Syn));
        await h.OpenInbound(h.InboundId + 2);
        Assert.That(h.Apps, Has.Count.EqualTo(listener ? 514 : 513));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InboundCapDoesNotBlockLocalStreamsAndOnlyInboundRemovalReleasesCapacity(bool listener)
    {
        await using Harness h = new(listener);
        for (int i = 0; i < 512; i++) await h.OpenInbound(h.InboundId + 2 * i);
        h.Requests.Add(new UpgradeOptions());
        YamuxHeader local = (await h.Next()).Header;
        Assert.That(local.Flags, Is.EqualTo(YamuxHeaderFlags.Syn));
        await h.Send(Update(local.StreamID, YamuxHeaderFlags.Rst));
        await h.Log.Wait("Stream " + local.StreamID + ": Closed", h.Token);
        await h.Send(Update(h.InboundId + 1024, YamuxHeaderFlags.Syn));
        Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst), "Removing a local stream must not release inbound capacity.");
        await h.Send(Update(h.InboundId, YamuxHeaderFlags.Rst));
        await h.Log.Wait("Stream " + h.InboundId + ": Closed", h.Token);
        await h.OpenInbound(h.InboundId + 1026);
        await h.Send(Update(h.InboundId + 1028, YamuxHeaderFlags.Syn));
        Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst), "Exactly one inbound slot was released.");
    }

    [TestCase(0)]
    [TestCase(3)]
    public async Task UnknownPayloadReadHasDeadlineAndNeverResumesAfterFailure(int partialBytes)
    {
        ManualClock clock = new();
        await using Harness h = new(false, clock);
        await h.Send(Data(77, 7), new byte[partialBytes]);
        CancellationToken operation = await h.Transport.PayloadRead.Task.WaitAsync(h.Token);
        Assert.That(operation.CanBeCanceled, Is.True, "The deadline must reach the actual read operation.");
        var deadline = await clock.NextTimerAsync(h.Token);
        Assert.That(deadline.DueTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
        deadline.Callback(deadline.State);
        await h.Transport.PayloadReturned.Task.WaitAsync(h.Token);
        Assert.That(operation.IsCancellationRequested, Is.True);
        // The catch-all may send InternalError before closing; the collector drains it.
        await h.Run.WaitAsync(h.Token);
        Assert.That(h.Transport.HeaderReads, Is.EqualTo(1), "A failed discard cannot resume at an unknown frame boundary.");
    }

    [TestCase(IOResult.Ended)]
    [TestCase(IOResult.Aborted)]
    [TestCase(IOResult.Cancelled)]
    public async Task UnknownPayloadNonOkReadTerminatesSession(IOResult failure)
    {
        await using Harness h = new(false);
        h.Transport.PayloadFailure = failure;
        await h.Send(Data(77, 7));
        await h.Transport.PayloadRead.Task.WaitAsync(h.Token);
        h.Transport.ReleaseFailure.TrySetResult();
        await h.Transport.PayloadReturned.Task.WaitAsync(h.Token);
        await h.Run.WaitAsync(h.Token);
        Assert.That(h.Transport.HeaderReads, Is.EqualTo(1));
    }

    [TestCase(false, 0)]
    [TestCase(true, 0)]
    [TestCase(false, 7)]
    [TestCase(true, 7)]
    public async Task UnknownOrRemovedPayloadIsDrainedExactly(bool removed, int length)
    {
        await using Harness h = new(false);
        int id = h.InboundId;
        if (removed)
        {
            await h.OpenInbound(id);
            await h.Send(Update(id, YamuxHeaderFlags.Rst));
            await h.Log.Wait("Stream " + id + ": Closed", h.Token);
        }
        await h.Send(Data(id, length));
        for (int i = 0; i < length; i++)
            Assert.That(await h.Remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { (byte)i }), h.Token), Is.EqualTo(IOResult.Ok));
        await h.Ping();
    }

    [TestCase(false, false, 0)]
    [TestCase(true, false, 0)]
    [TestCase(false, true, 37)]
    [TestCase(true, true, 37)]
    public async Task SendCreditComesFromProtocolDefaultAndPeerGrants(bool listener, bool localOpening, int openingDelta)
    {
        await using Harness h = new(listener, settings: new YamuxWindowSettings { InitialWindowSize = 524288 });
        int id;
        if (listener) await h.OpenInbound(h.InboundId);
        if (localOpening)
        {
            h.Requests.Add(new UpgradeOptions());
            var opening = await h.Next();
            id = opening.Header.StreamID;
            Assert.That(opening.Header.Length, Is.EqualTo(262144));
            await h.Send(Update(id, YamuxHeaderFlags.Ack, openingDelta));
        }
        else
        {
            id = listener ? h.InboundId + 2 : h.InboundId;
            await h.Send(Update(id, YamuxHeaderFlags.Syn, openingDelta));
            Assert.That((await h.Next()).Header.Length, Is.EqualTo(262144));
        }
        await h.Ping();
        IChannel app = h.Apps[^1].Reverse();
        int allowance = YamuxProtocol.ProtocolInitialWindowSize + openingDelta;
        byte[] bytes = new byte[allowance + 17];
        new Random(391).NextBytes(bytes);
        Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(bytes), h.Token), Is.EqualTo(IOResult.Ok));
        List<byte> received = [];
        while (received.Count < allowance)
        {
            var frame = await h.Next();
            Assert.That(frame.Header.Type, Is.EqualTo(YamuxHeaderType.Data));
            received.AddRange(frame.Payload);
        }
        Assert.That(received, Has.Count.EqualTo(allowance));
        await h.Log.Wait("Waiting for remote window credit", h.Token);
        Assert.That(h.HasFrames, Is.False, "The pump reached its credit wait without sending bytes beyond peer credit.");
        await h.Send(GoAway());
        await h.Ping();
        await h.Send(Update(id, delta: 11));
        var firstGrant = await h.Next();
        Assert.That(firstGrant.Payload, Has.Length.EqualTo(11));
        received.AddRange(firstGrant.Payload);
        await h.Send(Update(id, delta: 6));
        var secondGrant = await h.Next();
        Assert.That(secondGrant.Payload, Has.Length.EqualTo(6));
        received.AddRange(secondGrant.Payload);
        Assert.That(received, Is.EqualTo(bytes));
        Assert.That(await app.WriteEofAsync(h.Token), Is.EqualTo(IOResult.Ok));
        Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Fin));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AdmittedUpgradeRacingShutdownIsSettled(bool terminal)
    {
        await using Harness h = new(false);
        using ManualResetEventSlim release = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestChannel app = new();
        h.Session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException();
            return app;
        });
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Requests.Add(new UpgradeOptions { CompletionSource = completion });
        TaskCompletionSource<object?> queued = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await entered.Task.WaitAsync(h.Token);
            if (!terminal) h.Requests.Add(new UpgradeOptions { CompletionSource = queued });
            await h.Send(GoAway());
            await h.Ping();
            if (terminal)
            {
                await h.Transport.CloseAsync();
                await h.Run.WaitAsync(h.Token);
            }
            release.Set();
            if (terminal)
            {
                Assert.ThrowsAsync<ChannelClosedException>(async () => await completion.Task.WaitAsync(h.Token));
                Assert.That((await app.Reverse().ReadAsync(1, token: h.Token)).Result, Is.EqualTo(IOResult.Aborted));
            }
            else
            {
                Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Syn));
                Assert.That(await app.Reverse().WriteAsync(new ReadOnlySequence<byte>(new byte[] { 9 }), h.Token), Is.EqualTo(IOResult.Ok));
                Assert.That((await h.Next()).Payload, Is.EqualTo(new byte[] { 9 }));
                Assert.ThrowsAsync<ChannelClosedException>(async () => await queued.Task.WaitAsync(h.Token));
            }
        }
        finally { release.Set(); }
    }

    public enum Removal { Normal, PumpFailure, Idle, ConcurrentAbort }

    [TestCase(Removal.Normal)]
    [TestCase(Removal.PumpFailure)]
    [TestCase(Removal.Idle)]
    [TestCase(Removal.ConcurrentAbort)]
    public async Task EveryRemovalPathReleasesExactlyOneInboundSlot(Removal removal)
    {
        ManualClock clock = new();
        await using Harness h = new(false, clock, closedIdle: removal == Removal.Idle ? TimeSpan.FromSeconds(20) : null);
        for (int i = 0; i < 511; i++) await h.OpenInbound(h.InboundId + i * 2);
        int last = h.InboundId + 1022;
        if (removal == Removal.PumpFailure)
            h.UpgradeFactory = () => new FaultAppChannel(failReadsWith: _ => new IOException("application read failed"));
        await h.OpenInbound(last);
        h.UpgradeFactory = null;
        if (removal == Removal.Normal)
        {
            await h.Send(Update(last, YamuxHeaderFlags.Fin));
            IChannel app = h.Apps[^1].Reverse();
            Assert.That((await app.ReadAsync(1, token: h.Token)).Result, Is.EqualTo(IOResult.Ended));
            Assert.That(await app.WriteEofAsync(h.Token), Is.EqualTo(IOResult.Ok));
            Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Fin));
        }
        else if (removal == Removal.Idle)
        {
            IChannel app = h.Apps[^1].Reverse();
            Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(new byte[262145]), h.Token), Is.EqualTo(IOResult.Ok));
            int sent = 0;
            while (sent < 262144) sent += (await h.Next()).Header.Length;
            await h.Log.Wait("Waiting for remote window credit", h.Token);
            Assert.That(await app.WriteEofAsync(h.Token), Is.EqualTo(IOResult.Ok));
            await app.CloseAsync();
            var deadline = await clock.NextTimerAsync(h.Token);
            clock.Advance(TimeSpan.FromSeconds(21));
            deadline.Callback(deadline.State);
            Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
        }
        else if (removal == Removal.ConcurrentAbort)
        {
            await Task.WhenAll(h.Apps[^1].AbortAsync().AsTask(), h.Send(Update(last, YamuxHeaderFlags.Rst)));
        }
        else
        {
            Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
        }
        await h.Log.Wait("Stream " + last + ": Closed", h.Token);
        while (h.HasFrames) await h.Next();
        await h.OpenInbound(last + 2);
        await h.Send(Update(last + 4, YamuxHeaderFlags.Syn));
        Assert.That((await h.Next()).Header.Flags, Is.EqualTo(YamuxHeaderFlags.Rst));
    }

    [Test]
    public async Task FailedUpgradeTerminatesWithoutPublishingAStream()
    {
        await using Harness h = new(true);
        h.UpgradeFactory = () => throw new IOException("upgrade failed");
        await h.SendFinal(Update(1, YamuxHeaderFlags.Syn));
        Assert.That((await h.Next()).Header.Type, Is.EqualTo(YamuxHeaderType.GoAway));
        await h.Run.WaitAsync(h.Token);
        Assert.That(h.Apps, Is.Empty);
    }

    internal static YamuxHeader Update(int id, YamuxHeaderFlags flags = 0, int delta = 0) =>
        new() { Type = YamuxHeaderType.WindowUpdate, StreamID = id, Flags = flags, Length = delta };
    internal static YamuxHeader Data(int id, int length, YamuxHeaderFlags flags = 0) =>
        new() { Type = YamuxHeaderType.Data, StreamID = id, Flags = flags, Length = length };
    internal static YamuxHeader GoAway(int code = 0) => new() { Type = YamuxHeaderType.GoAway, Length = code };

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(8));
        private readonly CancellationTokenSource _sessionClosed = new();
        private readonly Task _collector;
        private readonly global::System.Threading.Channels.Channel<(YamuxHeader Header, byte[] Payload)> _frames =
            global::System.Threading.Channels.Channel.CreateUnbounded<(YamuxHeader, byte[])>();
        public CancellationToken Token => _timeout.Token;
        public ProbeTransport Transport { get; } = new();
        public IChannel Remote => Transport.Inner.Reverse();
        public IConnectionContext Context { get; } = Substitute.For<IConnectionContext>();
        public INewSessionContext Session { get; } = Substitute.For<INewSessionContext>();
        public BlockingCollection<UpgradeOptions> Requests { get; } = new();
        public List<TestChannel> Apps { get; } = [];
        public Func<IChannel>? UpgradeFactory { get; set; }
        public SignalLogger Log { get; } = new();
        public int InboundId { get; }
        public Task Run { get; }
        public bool HasFrames => _frames.Reader.TryPeek(out _);

        public Harness(bool listener, TimeProvider? clock = null, YamuxWindowSettings? settings = null, TimeSpan? closedIdle = null)
        {
            InboundId = listener ? 1 : 2;
            Context.State.Returns(new State());
            Context.UpgradeToSession().Returns(Session);
            Session.Id.Returns("admission-test");
            Session.State.Returns(new State());
            Session.Token.Returns(_sessionClosed.Token);
            Session.DialRequests.Returns(Requests.GetConsumingEnumerable(_sessionClosed.Token));
            Session.When(s => s.Dispose()).Do(_ => _sessionClosed.Cancel());
            Session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                if (UpgradeFactory is { } factory) return factory();
                TestChannel app = new();
                lock (Apps) Apps.Add(app);
                return app;
            });
            ILoggerFactory factory = Substitute.For<ILoggerFactory>();
            factory.CreateLogger(Arg.Any<string>()).Returns(Log);
            YamuxProtocol protocol = new(loggerFactory: factory, timeProvider: clock, windowSettings: settings, closedStreamIdleTimeout: closedIdle);
            Run = listener ? protocol.ListenAsync(Transport, Context) : protocol.DialAsync(Transport, Context);
            _collector = Collect();
        }

        private async Task Collect()
        {
            try
            {
                while (true)
                {
                    ReadResult read = await Remote.ReadAsync(12, token: Token);
                    if (read.Result != IOResult.Ok) break;
                    YamuxHeader header = YamuxHeader.FromBytes(read.Data.ToArray());
                    byte[] payload = header.Type == YamuxHeaderType.Data && header.Length > 0
                        ? (await Remote.ReadAsync(header.Length, token: Token).OrThrow()).ToArray() : [];
                    await _frames.Writer.WriteAsync((header, payload), Token);
                }
            }
            finally { _frames.Writer.TryComplete(); }
        }

        public async Task SendFinal(YamuxHeader header)
        {
            byte[] bytes = new byte[12];
            YamuxHeader.ToBytes(bytes, ref header);
            // Teardown may race the sender's continuation after consuming this header.
            // The expected GoAway and bounded completion below prove its receipt.
            await Remote.WriteAsync(new ReadOnlySequence<byte>(bytes), Token);
        }
        public async Task Send(YamuxHeader header, byte[]? payload = null)
        {
            byte[] bytes = new byte[12];
            YamuxHeader.ToBytes(bytes, ref header);
            Assert.That(await Remote.WriteAsync(new ReadOnlySequence<byte>(bytes), Token), Is.EqualTo(IOResult.Ok));
            if (payload is { Length: > 0 })
                Assert.That(await Remote.WriteAsync(new ReadOnlySequence<byte>(payload), Token), Is.EqualTo(IOResult.Ok));
        }
        public async Task<(YamuxHeader Header, byte[] Payload)> Next() => await _frames.Reader.ReadAsync(Token);
        public async Task OpenInbound(int id)
        {
            await Send(Update(id, YamuxHeaderFlags.Syn));
            var opening = await Next();
            Assert.That(opening.Header.StreamID, Is.EqualTo(id));
            Assert.That(opening.Header.Flags, Is.EqualTo(YamuxHeaderFlags.Ack));
        }
        public async Task Ping()
        {
            await Send(FuzzPingSyn(1234));
            var ack = await Next();
            Assert.That(ack.Header.Type, Is.EqualTo(YamuxHeaderType.Ping));
            Assert.That(ack.Header.Flags, Is.EqualTo(YamuxHeaderFlags.Ack));
            Assert.That(ack.Header.Length, Is.EqualTo(1234));
        }
        public async ValueTask DisposeAsync()
        {
            await Transport.CloseAsync();
            _sessionClosed.Cancel();
            Requests.CompleteAdding();
            await Run.WaitAsync(TimeSpan.FromSeconds(10));
            await _collector;
            _sessionClosed.Dispose();
            _timeout.Dispose();
            Requests.Dispose();
        }
    }

    internal sealed class ProbeTransport : IChannel
    {
        public TestChannel Inner { get; } = new();
        public TaskCompletionSource<CancellationToken> PayloadRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PayloadReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IOResult? PayloadFailure { get; set; }
        private int _headers;
        public int HeaderReads => Volatile.Read(ref _headers);
        public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode blockingMode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            if (length == 12) Interlocked.Increment(ref _headers);
            else PayloadRead.TrySetResult(token);
            ReadResult result;
            if (length != 12 && PayloadFailure is { } failure)
            {
                await ReleaseFailure.Task.WaitAsync(token);
                result = new ReadResult { Result = failure };
            }
            else result = await Inner.ReadAsync(length, blockingMode, token);
            if (length != 12) PayloadReturned.TrySetResult();
            return result;
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default) => Inner.WriteAsync(bytes, token);
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => Inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => Inner.CloseAsync();
        public ValueTask AbortAsync() => Inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => Inner.GetAwaiter();
    }

    internal sealed class SignalLogger : ILogger
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _signals = new();
        private readonly ConcurrentDictionary<string, byte> _seen = new();
        public async Task Wait(string message, CancellationToken token)
        {
            TaskCompletionSource signal = _signals.GetOrAdd(message, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (_seen.ContainsKey(message)) return;
            await signal.Task.WaitAsync(token);
        }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception);
            if (text.EndsWith(": Waiting for remote window credit", StringComparison.Ordinal))
            {
                _seen.TryAdd("Waiting for remote window credit", 0);
                if (_signals.TryGetValue("Waiting for remote window credit", out TaskCompletionSource? waiting)) waiting.TrySetResult();
            }
            // The existing cleanup log is the barrier proving table removal completed.
            int start = text.IndexOf("stream ", StringComparison.Ordinal);
            if (start >= 0 && text.EndsWith(": Closed", StringComparison.Ordinal))
            {
                string message = "Stream " + text[(start + 7)..];
                _seen.TryAdd(message, 0);
                if (_signals.TryGetValue(message, out TaskCompletionSource? signal)) signal.TrySetResult();
            }
        }
    }
}
