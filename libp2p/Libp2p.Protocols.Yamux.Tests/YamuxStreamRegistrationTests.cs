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
/// Coverage for locally opened streams and the issue-#298 family: an immediate
/// peer reply (ACK, RST, data, window credit, FIN) for a dial-request stream
/// must be processed against the registered stream, never discarded as a frame
/// for a closed stream. Plus end-to-end data integrity through the pumps and
/// the half-close-then-abort reset path. Deterministic; no new dependencies.
/// </summary>
[TestFixture]
public class YamuxStreamRegistrationTests
{
    public static IEnumerable<int> Seeds()
    {
        for (int seed = 3001; seed <= 3010; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public async Task ImmediateReply_ForLocallyOpenedStream_IsProcessed(int seed)
    {
        // Reply kinds: 0 = ACK, 1 = RST, 2 = data, 3 = window credit, 4 = FIN, 5 = ACK + data.
        int kind = seed % 6;
        (IConnectionContext ctx, List<TestChannel> ups) =
            DialerWithRequests(1, "dialer-immediate");
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = await WaitForOutboundSynAsync(outbound, timeout.Token);
            byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
            if (kind is 0 or 5)
            {
                await WriteHeaderAsync(remote, AckFor(syn.StreamID), timeout.Token);
            }
            if (kind == 1)
            {
                await WriteHeaderAsync(remote, RstFor(syn.StreamID), timeout.Token);
            }
            if (kind is 2 or 5)
            {
                await WriteHeaderAsync(remote, DataFor(syn.StreamID, payload.Length), timeout.Token);
                Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token),
                    Is.EqualTo(IOResult.Ok));
            }
            if (kind == 3)
            {
                await WriteHeaderAsync(remote, new YamuxHeader
                {
                    Type = YamuxHeaderType.WindowUpdate,
                    StreamID = syn.StreamID,
                    Length = 1000
                }, timeout.Token);
            }
            if (kind == 4)
            {
                await WriteHeaderAsync(remote, new YamuxHeader
                {
                    Type = YamuxHeaderType.WindowUpdate,
                    Flags = YamuxHeaderFlags.Fin,
                    StreamID = syn.StreamID
                }, timeout.Token);
            }

            TestChannel app = await WaitForUpchannelAsync(ups, timeout.Token);
            if (kind == 1)
            {
                Assert.That((await app.Reverse().ReadAsync(1, token: timeout.Token)).Result,
                    Is.EqualTo(IOResult.Aborted), "RST must abort the locally opened stream.");
            }
            if (kind is 2 or 5)
            {
                ReadOnlySequence<byte> received =
                    await app.Reverse().ReadAsync(payload.Length, token: timeout.Token).OrThrow();
                Assert.That(received.ToArray(), Is.EqualTo(payload),
                    "Immediate data must reach the locally opened stream, not be discarded.");
            }
            await WriteHeaderAsync(remote, FuzzPingSyn(31), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 31, timeout.Token), Is.Not.Null,
                "Session must survive the immediate reply.");
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
    public async Task ImmediateReply_RaceLoop_DataAlwaysDelivered()
    {
        // Probabilistic regression for issue #298: on the pre-fix ordering, an
        // immediate reply could land before dictionary publication and be
        // discarded. 100 fresh sessions; every single reply must be delivered.
        for (int iter = 0; iter < 100; iter++)
        {
            (IConnectionContext ctx, List<TestChannel> ups) =
                DialerWithRequests(1, $"dialer-race-{iter}");
            TestChannel transport = new();
            Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            try
            {
                // No drain here: the only outbound frame is the SYN, read below.
                // A concurrent drain reader would steal it.
                IChannel remote = transport.Reverse();
                YamuxHeader syn = await ReadOutboundHeaderAsync(remote, timeout.Token);
                byte[] payload = [(byte)iter, 2, 3, 4, 5, 6, 7, 8];
                await WriteHeaderAsync(remote, AckFor(syn.StreamID), timeout.Token);
                await WriteHeaderAsync(remote, DataFor(syn.StreamID, payload.Length), timeout.Token);
                Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token),
                    Is.EqualTo(IOResult.Ok));
                TestChannel app = await WaitForUpchannelAsync(ups, timeout.Token);
                ReadOnlySequence<byte> received =
                    await app.Reverse().ReadAsync(payload.Length, token: timeout.Token).OrThrow();
                Assert.That(received.ToArray(), Is.EqualTo(payload), $"Iteration {iter}: reply discarded.");
            }
            finally
            {
                await transport.CloseAsync();
                await yamux.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(yamux.IsFaulted, Is.False);
            }
        }
    }

    [TestCaseSource(nameof(Seeds))]
    public async Task EndToEnd_StreamDataIntegrity(int seed)
    {
        // Full-duplex byte-exact transfer above the initial window on both sides,
        // forcing window grants, segmentation, and FIN in both directions.
        IProtocol protocol = Mocks.TestProtocol();
        (IConnectionContext dialerCtx, List<TestChannel> dialerUps) =
            DialerWithRequests(1, "dialer-e2e", protocol);
        (IConnectionContext listenerCtx, List<TestChannel> listenerUps) =
            ListenerWithMocks("listener-e2e", protocol);

        TestChannel transport = new();
        YamuxProtocol yamux = new();
        Task listen = yamux.ListenAsync(transport.Reverse(), listenerCtx);
        Task dial = yamux.DialAsync(transport, dialerCtx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            TestChannel dialerApp = await WaitForUpchannelAsync(dialerUps, timeout.Token);
            TestChannel listenerApp = await WaitForUpchannelAsync(listenerUps, timeout.Token);
            Random rng = new(seed);
            byte[] toListener = new byte[300 * 1024];
            byte[] toDialer = new byte[300 * 1024];
            rng.NextBytes(toListener);
            rng.NextBytes(toDialer);
            Task<byte[]> collectFromListener = CollectAsync(listenerApp.Reverse(), toListener.Length, timeout.Token);
            Task<byte[]> collectFromDialer = CollectAsync(dialerApp.Reverse(), toDialer.Length, timeout.Token);
            Task sendToListener = SendChunkedAsync(dialerApp.Reverse(), toListener, 16 * 1024, timeout.Token);
            Task sendToDialer = SendChunkedAsync(listenerApp.Reverse(), toDialer, 16 * 1024, timeout.Token);
            await Task.WhenAll(sendToListener, sendToDialer).WaitAsync(timeout.Token);
            Assert.That(await collectFromListener.WaitAsync(timeout.Token), Is.EqualTo(toListener));
            Assert.That(await collectFromDialer.WaitAsync(timeout.Token), Is.EqualTo(toDialer));
            Assert.That(await dialerApp.Reverse().WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That(await listenerApp.Reverse().WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That((await dialerApp.Reverse().ReadAsync(1, token: timeout.Token)).Result,
                Is.EqualTo(IOResult.Ended));
            Assert.That((await listenerApp.Reverse().ReadAsync(1, token: timeout.Token)).Result,
                Is.EqualTo(IOResult.Ended));
        }
        finally
        {
            await transport.CloseAsync();
            await Task.WhenAll(listen, dial).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task HalfCloseThenAbort_SendsReset()
    {
        // App EOF sends FIN; a later local abort must surface as RST (covers the
        // half-closed abort path), and the session must stay alive.
        (IConnectionContext ctx, List<TestChannel> ups) =
            DialerWithRequests(1, "dialer-half-close");
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        Task drain = DrainAsync(transport.Reverse(), outbound, new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = await WaitForOutboundSynAsync(outbound, timeout.Token);
            TestChannel app = await WaitForUpchannelAsync(ups, timeout.Token);
            Assert.That(await app.Reverse().WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That(await WaitForFlagAsync(outbound, YamuxHeaderFlags.Fin, syn.StreamID, timeout.Token),
                Is.Not.Null, "EOF must send FIN.");
            await app.AbortAsync();
            Assert.That(await WaitForFlagAsync(outbound, YamuxHeaderFlags.Rst, syn.StreamID, timeout.Token),
                Is.Not.Null, "Abort after FIN must send RST.");
            await WriteHeaderAsync(remote, FuzzPingSyn(33), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 33, timeout.Token), Is.Not.Null);
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
    public async Task SlowUpgrade_ReplyBeforeRegistrationIsIgnored_LaterFramesWork()
    {
        // If stream creation itself (session.Upgrade) is slow, the peer cannot
        // yet know the stream id, so frames arriving before registration are
        // correctly ignored as unknown-stream frames — and the stream works
        // normally once creation completes. This pins the safe behavior.
        TaskCompletionSource releaseUpgrade = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource frameIgnored = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IProtocol slowProtocol = Mocks.TestProtocol();
        IConnectionContext context = Substitute.For<IConnectionContext>();
        INewSessionContext session = Substitute.For<INewSessionContext>();
        context.UpgradeToSession().Returns(session);
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.Id.Returns("dialer-slow-upgrade");
        session.DialRequests.Returns([new UpgradeOptions { SelectedProtocol = slowProtocol }]);
        session.SubProtocols.Returns([slowProtocol]);
        TestChannel appChannel = new();
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            releaseUpgrade.Task.Wait(TimeSpan.FromSeconds(10));
            return appChannel;
        });
        ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
        IgnoredFrameLogger logger = new(frameIgnored);
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);

        TestChannel transport = new();
        Task yamux = new YamuxProtocol(loggerFactory: loggerFactory).DialAsync(transport, context);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            IChannel remote = transport.Reverse();
            // No SYN is on the wire yet (creation blocked), but feed the reply
            // the peer would send for stream 1 anyway: it must be discarded.
            byte[] early = [1, 2, 3, 4, 5, 6, 7, 8];
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.Data,
                StreamID = 1,
                Length = early.Length
            }, timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(early), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            await frameIgnored.Task.WaitAsync(timeout.Token);

            releaseUpgrade.TrySetResult();
            YamuxHeader syn = await ReadOutboundHeaderAsync(remote, timeout.Token);
            Assert.That(syn.StreamID, Is.EqualTo(1));
            byte[] late = [9, 10, 11, 12, 13, 14, 15, 16];
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.Data,
                StreamID = 1,
                Length = late.Length
            }, timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(late), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            ReadOnlySequence<byte> received =
                await appChannel.Reverse().ReadAsync(late.Length, token: timeout.Token).OrThrow();
            Assert.That(received.ToArray(), Is.EqualTo(late),
                "Post-registration frames must be delivered exactly once.");
        }
        finally
        {
            releaseUpgrade.TrySetResult();
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
    }

    private sealed class IgnoredFrameLogger(TaskCompletionSource ignored) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("Ignored for closed stream", StringComparison.Ordinal))
            {
                ignored.TrySetResult();
            }
        }
    }

    private static (IConnectionContext Ctx, List<TestChannel> Ups) DialerWithRequests(
        int count, string id, IProtocol? protocol = null)
    {
        protocol ??= Mocks.TestProtocol();
        IConnectionContext context = Substitute.For<IConnectionContext>();
        INewSessionContext session = Substitute.For<INewSessionContext>();
        context.UpgradeToSession().Returns(session);
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
        session.Id.Returns(id);
        List<UpgradeOptions> requests = [];
        for (int i = 0; i < count; i++)
        {
            requests.Add(new UpgradeOptions { SelectedProtocol = protocol });
        }
        session.DialRequests.Returns(requests);
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

    private static (IConnectionContext Ctx, List<TestChannel> Ups) ListenerWithMocks(string id, IProtocol protocol)
    {
        IConnectionContext context = Substitute.For<IConnectionContext>();
        INewSessionContext session = Substitute.For<INewSessionContext>();
        context.UpgradeToSession().Returns(session);
        context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
        session.Id.Returns(id);
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

    private static YamuxHeader AckFor(int streamId) => new()
    {
        Type = YamuxHeaderType.WindowUpdate,
        Flags = YamuxHeaderFlags.Ack,
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

    private static async Task<TestChannel> WaitForUpchannelAsync(
        List<TestChannel> ups, CancellationToken token)
    {
        for (int i = 0; i < 200; i++)
        {
            lock (ups)
            {
                if (ups.Count > 0)
                {
                    return ups[0];
                }
            }
            await Task.Delay(25, token);
        }
        throw new TimeoutException("Upchannel was not created.");
    }

    private static async Task<YamuxHeader> WaitForOutboundSynAsync(
        List<FuzzObservation> outbound, CancellationToken token)
    {
        for (int i = 0; i < 200; i++)
        {
            lock (outbound)
            {
                FuzzObservation? syn = outbound.FirstOrDefault(o =>
                    (o.Header.Flags & YamuxHeaderFlags.Syn) != 0 && o.Header.StreamID != 0);
                if (syn is not null)
                {
                    return syn.Header;
                }
            }
            await Task.Delay(25, token);
        }
        throw new TimeoutException("Outbound SYN was not observed.");
    }

    private static async Task<YamuxHeader> ReadOutboundHeaderAsync(IChannel remote, CancellationToken token)
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

    private static async Task<byte[]> CollectAsync(IChannel app, int total, CancellationToken token)
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
}
