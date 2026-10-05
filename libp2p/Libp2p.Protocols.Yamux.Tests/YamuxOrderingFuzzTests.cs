// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using static Nethermind.Libp2p.Protocols.Yamux.Tests.YamuxFaultInjectionTests;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

/// <summary>
/// Forced-ordering coverage for the stream-lifecycle race family: cleanup vs
/// late frames, stream-id reuse atomicity, outbound-pump block/cancel matrix,
/// grant-driven flow control in both directions, and flood behavior (ping load
/// and stream-acceptance bounds). Deterministic; no new dependencies.
/// </summary>
[TestFixture]
public class YamuxOrderingFuzzTests
{
    [Test]
    public async Task CleanupParked_LateFrameSafe_ReuseAfterCloseWorks()
    {
        // Park the outbound FIN behind a gate so cleanup cannot remove the
        // stream while a late frame arrives; then release and reuse the id.
        // Either order must be safe and the reused stream must work.
        (IConnectionContext ctx, List<TestChannel> ups) = ListenerWithUps();
        GatedTransport transport = new(blockedWriteIndex: 2);
        ClosedLogger closedLog = new(streamId: 1);
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(closedLog);
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory).ListenAsync(transport, ctx);
        List<FuzzObservation> outbound = [];
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        try
        {
            IChannel remote = transport.Reverse();
            await WriteHeaderAsync(remote, SynFor(1), timeout.Token);
            TestChannel app = await WaitForUpsAsync(ups, 1, timeout.Token);
            await app.Reverse().CloseAsync();
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);

            // Late frame while cleanup is parked: entry still present, channel
            // half-closed. Must be absorbed without fault or GoAway.
            byte[] late = [1, 2, 3, 4];
            await WriteHeaderAsync(remote, DataFor(1, late.Length), timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(late), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            Assert.That(CountUps(ups), Is.EqualTo(1), "Parked cleanup must not duplicate the stream.");
            await WriteHeaderAsync(remote, FuzzPingSyn(41), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 41, timeout.Token), Is.Not.Null);
            Assert.That(FirstGoAway(outbound), Is.Null);

            transport.Release();
            await closedLog.Closed.Task.WaitAsync(timeout.Token);

            // Reuse after removal: a fresh stream must be created and work.
            await WriteHeaderAsync(remote, SynFor(1), timeout.Token);
            await WaitForUpsAsync(ups, 2, timeout.Token);
            byte[] fresh = [5, 6, 7, 8];
            await WriteHeaderAsync(remote, DataFor(1, fresh.Length), timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(fresh), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            TestChannel app2;
            lock (ups) { app2 = ups[1]; }
            ReadOnlySequence<byte> received =
                await app2.Reverse().ReadAsync(fresh.Length, token: timeout.Token).OrThrow();
            Assert.That(received.ToArray(), Is.EqualTo(fresh));
        }
        finally
        {
            transport.Release();
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    [Test]
    public async Task PumpBlockedInSynWrite_RstProcessedOnRelease()
    {
        // Hold the initial SYN behind a gate; the RST must still abort the
        // (registered) stream, and the released SYN must not resurrect it.
        (IConnectionContext ctx, List<TestChannel> ups) = DialerWithOneRequest();
        GatedTransport transport = new(blockedWriteIndex: 1);
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        List<FuzzObservation> outbound = [];
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            IChannel remote = transport.Reverse();
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);
            await WriteHeaderAsync(remote, RstFor(1), timeout.Token);
            TestChannel app = await WaitForUpsAsync(ups, 1, timeout.Token);
            Assert.That((await app.Reverse().ReadAsync(1, token: timeout.Token)).Result,
                Is.EqualTo(IOResult.Aborted), "RST must abort even with the SYN still gated.");
            transport.Release();
            // The abort wins the race deterministically: the gated SYN write is
            // cancelled, so no SYN must ever hit the wire for the reset stream.
            await Task.Delay(500, timeout.Token);
            lock (outbound)
            {
                Assert.That(outbound.Any(o => o.Header.StreamID == 1 &&
                    (o.Header.Flags & YamuxHeaderFlags.Syn) != 0), Is.False,
                    "Aborted stream must not send its gated SYN.");
            }
            await WriteHeaderAsync(remote, FuzzPingSyn(43), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 43, timeout.Token), Is.Not.Null);
            Assert.That(FirstGoAway(outbound), Is.Null);
        }
        finally
        {
            transport.Release();
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    [Test]
    public async Task PumpBlockedInDataWrite_RstCancelsPump()
    {
        // Observe the SYN before sending RST, then hold the DATA write at the
        // transport so the reset must cancel an established stream's pump.
        (IConnectionContext ctx, List<TestChannel> ups) = DialerWithOneRequest();
        GatedTransport transport = new(blockedWriteIndex: 2);
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            Assert.That(syn.StreamID, Is.EqualTo(1));
            Assert.That(syn.Flags, Is.EqualTo(YamuxHeaderFlags.Syn));
            TestChannel app = await WaitForUpsAsync(ups, 1, timeout.Token);
            Assert.That(await app.Reverse().WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0xA5 }), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            await transport.WriteBlocked.Task.WaitAsync(timeout.Token);
            await WriteHeaderAsync(remote, RstFor(syn.StreamID), timeout.Token);
            Assert.That((await app.Reverse().ReadAsync(1, token: timeout.Token)).Result,
                Is.EqualTo(IOResult.Aborted));
            await transport.WriteCancelled.Task.WaitAsync(timeout.Token);
            Assert.That(transport.CancelledWriteCount, Is.EqualTo(1));
        }
        finally
        {
            transport.Release();
            await transport.Inner.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
    }

    [TestCase(7001)]
    [TestCase(7002)]
    public async Task FlowControl_SeededGrantsDriveLargeUpload(int seed)
    {
        // Sender starts with 256 KiB credit; the test feeds a seeded schedule
        // of window grants and must receive every byte back on the wire.
        const int total = 512 * 1024;
        (IConnectionContext ctx, List<TestChannel> ups) = DialerWithOneRequest();
        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            IChannel remote = transport.Reverse();
            TestChannel app = await WaitForUpsAsync(ups, 1, timeout.Token);
            byte[] payload = new byte[total];
            new Random(seed).NextBytes(payload);
            Task send = SendChunkedAsync(app.Reverse(), payload, 16 * 1024, timeout.Token);
            byte[] collected = new byte[total];
            int offset = 0;
            Random grants = new(seed * 31 + 7);
            int[] sizes = [8 * 1024, 16 * 1024, 32 * 1024, 64 * 1024];
            while (offset < total)
            {
                using CancellationTokenSource stall = new(TimeSpan.FromSeconds(2));
                ReadResult headerRead = await remote.ReadAsync(12, token: stall.Token);
                if (headerRead.Result != IOResult.Ok)
                {
                    // Sender stalled waiting for credit: grant more and retry.
                    await WriteHeaderAsync(remote, new YamuxHeader
                    {
                        Type = YamuxHeaderType.WindowUpdate,
                        StreamID = 1,
                        Length = sizes[grants.Next(sizes.Length)]
                    }, timeout.Token);
                    continue;
                }
                YamuxHeader header = YamuxHeader.FromBytes(headerRead.Data.ToArray());
                if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                {
                    ReadOnlySequence<byte> chunk =
                        await remote.ReadAsync(header.Length, token: timeout.Token).OrThrow();
                    chunk.CopyTo(collected.AsSpan(offset));
                    offset += (int)chunk.Length;
                }
            }
            await send.WaitAsync(timeout.Token);
            Assert.That(collected.SequenceEqual(payload), Is.True, "Grant-driven upload must be byte-exact.");
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
    }

    [Test]
    public async Task FlowControl_LargeDownload_AutoGrants()
    {
        // Inbound 512 KiB in small frames: the receiver must auto-extend and
        // advertise grants while delivering every byte to the application.
        const int total = 512 * 1024;
        (IConnectionContext ctx, List<TestChannel> ups) = ListenerWithUps();
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().ListenAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            IChannel remote = transport.Reverse();
            await WriteHeaderAsync(remote, SynFor(1), timeout.Token);
            TestChannel app = await WaitForUpsAsync(ups, 1, timeout.Token);
            byte[] payload = new byte[total];
            new Random(8001).NextBytes(payload);
            Task<byte[]> collect = CollectExactAsync(app.Reverse(), total, timeout.Token);
            for (int offset = 0; offset < total; offset += 4096)
            {
                int size = Math.Min(4096, total - offset);
                byte[] slice = new byte[size];
                payload.AsSpan(offset, size).CopyTo(slice);
                await WriteHeaderAsync(remote, DataFor(1, size), timeout.Token);
                Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(slice), timeout.Token),
                    Is.EqualTo(IOResult.Ok));
            }
            Assert.That(await collect.WaitAsync(timeout.Token), Is.EqualTo(payload));
            int grants;
            lock (outbound)
            {
                grants = outbound.Count(o =>
                    o.Header.Type == YamuxHeaderType.WindowUpdate && o.Header.Length > 0);
            }
            Assert.That(grants, Is.GreaterThan(0), "Receiver must advertise window grants under load.");
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
    public async Task PingFlood_AllAcknowledged()
    {
        (IConnectionContext ctx, _) = Mocks.Dialer([]);
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            const int count = 500;
            IChannel remote = transport.Reverse();
            for (int i = 0; i < count; i++)
            {
                await WriteHeaderAsync(remote, FuzzPingSyn(i), timeout.Token);
            }
            HashSet<int> acknowledged = [];
            for (int i = 0; i < 600 && acknowledged.Count < count; i++)
            {
                await Task.Delay(25, timeout.Token);
                lock (outbound)
                {
                    foreach (FuzzObservation o in outbound)
                    {
                        if (o.Header.Type == YamuxHeaderType.Ping &&
                            (o.Header.Flags & YamuxHeaderFlags.Ack) != 0)
                        {
                            acknowledged.Add(o.Header.Length);
                        }
                    }
                }
            }
            Assert.That(acknowledged.Count, Is.EqualTo(count), "Every ping must be acknowledged.");
            for (int i = 0; i < count; i++)
            {
                Assert.That(acknowledged, Does.Contain(i), $"Ping {i} was not acknowledged.");
            }
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
    public async Task SynFlood_OverCapRejectedWithRst()
    {
        // The stream table is capped: the first 512 inbound SYNs are accepted
        // and anything beyond is rejected with RST instead of allocating state.
        const int overCap = 520;
        const int cap = 512;
        (IConnectionContext ctx, List<TestChannel> ups) = ListenerWithUps();
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().ListenAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        try
        {
            long before = GC.GetTotalMemory(forceFullCollection: true);
            IChannel remote = transport.Reverse();
            byte[] blob = new byte[overCap * 12];
            for (int i = 0; i < overCap; i++)
            {
                YamuxHeader syn = SynFor(1 + 2 * i);
                YamuxHeader copy = syn;
                YamuxHeader.ToBytes(blob.AsSpan(i * 12, 12), ref copy);
            }
            await FeedBlobAsync(remote, blob, new Random(9001), timeout.Token);
            for (int i = 0; i < 400 && CountUps(ups) < cap; i++)
            {
                await Task.Delay(25, timeout.Token);
            }
            long after = GC.GetTotalMemory(forceFullCollection: false);
            Assert.That(CountUps(ups), Is.EqualTo(cap), "Stream table must stop growing at the cap.");
            int lastRejectedId = 1 + 2 * (overCap - 1);
            Assert.That(await WaitForFlagAsync(outbound, YamuxHeaderFlags.Rst, lastRejectedId, timeout.Token),
                Is.Not.Null, "Over-cap SYN must be rejected with RST.");
            int rejectedDataId = 1 + 2 * overCap;
            YamuxHeader dataSyn = DataFor(rejectedDataId, 3);
            dataSyn.Flags = YamuxHeaderFlags.Syn;
            await WriteHeaderAsync(remote, dataSyn, timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0xAA, 0xBB, 0xCC }), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            Assert.That(await WaitForFlagAsync(outbound, YamuxHeaderFlags.Rst, rejectedDataId, timeout.Token),
                Is.Not.Null, "Over-cap DATA|SYN must be rejected with RST.");
            TestContext.Out.WriteLine($"SynFlood: {CountUps(ups)} streams held, GC heap delta {(after - before) / 1024} KiB.");
            await WriteHeaderAsync(remote, FuzzPingSyn(45), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 45, timeout.Token), Is.Not.Null,
                "Session must stay responsive at the cap.");
            Assert.That(FirstGoAway(outbound), Is.Null);
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    [Test]
    public async Task StreamSlotFreed_AllowsNewStream()
    {
        // A rejected id becomes acceptable once a slot frees up.
        const int cap = 512;
        (IConnectionContext ctx, List<TestChannel> ups) = ListenerWithUps();
        ClosedLogger closedLog = new(streamId: 1);
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(closedLog);
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory).ListenAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        try
        {
            IChannel remote = transport.Reverse();
            byte[] blob = new byte[(cap + 1) * 12];
            for (int i = 0; i <= cap; i++)
            {
                YamuxHeader syn = SynFor(1 + 2 * i);
                YamuxHeader copy = syn;
                YamuxHeader.ToBytes(blob.AsSpan(i * 12, 12), ref copy);
            }
            await FeedBlobAsync(remote, blob, new Random(9002), timeout.Token);
            for (int i = 0; i < 400 && CountUps(ups) < cap; i++)
            {
                await Task.Delay(25, timeout.Token);
            }
            Assert.That(CountUps(ups), Is.EqualTo(cap));
            int rejectedId = 1 + 2 * cap;
            Assert.That(await WaitForFlagAsync(outbound, YamuxHeaderFlags.Rst, rejectedId, timeout.Token),
                Is.Not.Null, "The 513th SYN must be rejected.");

            TestChannel first;
            lock (ups) { first = ups[0]; }
            await first.Reverse().CloseAsync();
            await closedLog.Closed.Task.WaitAsync(timeout.Token);

            await WriteHeaderAsync(remote, SynFor(rejectedId), timeout.Token);
            await WaitForUpsAsync(ups, cap + 1, timeout.Token);
            byte[] payload = [7, 7, 7];
            await WriteHeaderAsync(remote, DataFor(rejectedId, payload.Length), timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            TestChannel fresh;
            lock (ups) { fresh = ups[^1]; }
            ReadOnlySequence<byte> received =
                await fresh.Reverse().ReadAsync(payload.Length, token: timeout.Token).OrThrow();
            Assert.That(received.ToArray(), Is.EqualTo(payload));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    private static YamuxHeader SynFor(int streamId) => new()
    {
        Type = YamuxHeaderType.WindowUpdate,
        Flags = YamuxHeaderFlags.Syn,
        StreamID = streamId
    };

    private static YamuxHeader RstFor(int streamId) => new()
    {
        Type = YamuxHeaderType.WindowUpdate,
        Flags = YamuxHeaderFlags.Rst,
        StreamID = streamId
    };

    private static YamuxHeader DataFor(int streamId, int length) => new()
    {
        Type = YamuxHeaderType.Data,
        StreamID = streamId,
        Length = length
    };

    private static (IConnectionContext Ctx, List<TestChannel> Ups) ListenerWithUps()
    {
        (IConnectionContext ctx, INewSessionContext session) = Mocks.Listener();
        List<TestChannel> ups = [];
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            TestChannel up = new();
            lock (ups) { ups.Add(up); }
            return up;
        });
        return (ctx, ups);
    }

    private static (IConnectionContext Ctx, List<TestChannel> Ups) DialerWithOneRequest()
    {
        IProtocol protocol = Mocks.TestProtocol();
        IConnectionContext context = Substitute.For<IConnectionContext>();
        INewSessionContext session = Substitute.For<INewSessionContext>();
        context.UpgradeToSession().Returns(session);
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.Id.Returns("ordering-dialer");
        session.DialRequests.Returns([new UpgradeOptions { SelectedProtocol = protocol }]);
        session.SubProtocols.Returns([protocol]);
        List<TestChannel> ups = [];
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            TestChannel up = new();
            lock (ups) { ups.Add(up); }
            return up;
        });
        return (context, ups);
    }

    private static int CountUps(List<TestChannel> ups)
    {
        lock (ups) { return ups.Count; }
    }

    private static async Task<TestChannel> WaitForUpsAsync(
        List<TestChannel> ups, int count, CancellationToken token)
    {
        for (int i = 0; i < 400; i++)
        {
            lock (ups)
            {
                if (ups.Count >= count)
                {
                    return ups[^1];
                }
            }
            await Task.Delay(25, token);
        }
        throw new TimeoutException($"Only {CountUps(ups)}/{count} upstream channels created.");
    }

    private static async Task<YamuxHeader> WaitForSynAsync(
        List<FuzzObservation> outbound, int streamId, CancellationToken token)
    {
        for (int i = 0; i < 400; i++)
        {
            lock (outbound)
            {
                FuzzObservation? syn = outbound.FirstOrDefault(o =>
                    o.Header.StreamID == streamId && (o.Header.Flags & YamuxHeaderFlags.Syn) != 0);
                if (syn is not null)
                {
                    return syn.Header;
                }
            }
            await Task.Delay(25, token);
        }
        throw new TimeoutException("SYN was not observed.");
    }

    private static async Task<YamuxHeader> ReadHeaderAsync(IChannel remote, CancellationToken token)
    {
        YamuxHeader header = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: token).OrThrow()).ToArray());
        if (header.Type == YamuxHeaderType.Data && header.Length > 0)
        {
            await remote.ReadAsync(header.Length, token: token).OrThrow();
        }
        return header;
    }

    private static async Task SendChunkedAsync(IChannel app, byte[] payload, int chunk, CancellationToken token)
    {
        for (int offset = 0; offset < payload.Length; offset += chunk)
        {
            int size = Math.Min(chunk, payload.Length - offset);
            byte[] slice = new byte[size];
            payload.AsSpan(offset, size).CopyTo(slice);
            Assert.That(await app.WriteAsync(new ReadOnlySequence<byte>(slice), token), Is.EqualTo(IOResult.Ok));
        }
    }

    private static async Task<byte[]> CollectExactAsync(IChannel app, int total, CancellationToken token)
    {
        byte[] collected = new byte[total];
        int offset = 0;
        while (offset < total)
        {
            ReadOnlySequence<byte> chunk = await app.ReadAsync(total - offset, token: token).OrThrow();
            chunk.CopyTo(collected.AsSpan(offset));
            offset += (int)chunk.Length;
        }
        return collected;
    }

    private static async Task FeedBlobAsync(IChannel remote, byte[] blob, Random rng, CancellationToken token)
    {
        int offset = 0;
        while (offset < blob.Length)
        {
            int size = Math.Min(rng.Next(1, 100), blob.Length - offset);
            byte[] chunk = new byte[size];
            blob.AsSpan(offset, size).CopyTo(chunk);
            IOResult written = await remote.WriteAsync(new ReadOnlySequence<byte>(chunk), token);
            if (written != IOResult.Ok)
            {
                break;
            }
            offset += size;
        }
    }

    private sealed class ClosedLogger(int streamId) : ILogger
    {
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains($"stream {streamId}: Closed", StringComparison.Ordinal))
            {
                Closed.TrySetResult();
            }
        }
    }
}
