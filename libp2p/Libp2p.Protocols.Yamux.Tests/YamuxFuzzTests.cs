// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Yamux;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

/// <summary>
/// In-process deterministic fuzzer for <see cref="YamuxProtocol"/>.
/// Drives <c>ListenAsync</c>/<c>DialAsync</c> through the <see cref="TestChannel"/>
/// transport with generated frame sequences and raw byte streams, split into
/// random chunks to simulate TCP segmentation and interleaved partial frames.
///
/// The corpus is the committed seed list in <see cref="ProtocolSeeds"/> /
/// <see cref="RawByteSeeds"/>: every run is reproducible from its seed.
/// No new dependencies; runs as plain NUnit tests (CI-friendly).
/// </summary>
[TestFixture]
public class YamuxFuzzTests
{
    /// <summary>Committed corpus: seeds for the mixed-frame session fuzzer.</summary>
    public static IEnumerable<int> ProtocolSeeds()
    {
        for (int seed = 101; seed <= 120; seed++)
        {
            yield return seed;
        }
    }

    /// <summary>Committed corpus: seeds for the raw-byte-stream fuzzer.</summary>
    public static IEnumerable<int> RawByteSeeds()
    {
        for (int seed = 201; seed <= 210; seed++)
        {
            yield return seed;
        }
    }

    [TestCaseSource(nameof(ProtocolSeeds))]
    public async Task ProtocolFuzz_MixedFrames_DialerSide(int seed) =>
        await RunMixedFrameFuzz(seed, isListener: false);

    [TestCaseSource(nameof(ProtocolSeeds))]
    public async Task ProtocolFuzz_MixedFrames_ListenerSide(int seed) =>
        await RunMixedFrameFuzz(seed, isListener: true);

    [TestCaseSource(nameof(RawByteSeeds))]
    public async Task ProtocolFuzz_RawBytes_DialerSide(int seed) =>
        await RunRawByteFuzz(seed, isListener: false);

    [TestCaseSource(nameof(RawByteSeeds))]
    public async Task ProtocolFuzz_RawBytes_ListenerSide(int seed) =>
        await RunRawByteFuzz(seed, isListener: true);

    [Test]
    public async Task Fuzz_UnknownType_ClosesSessionWithProtocolError(
        [Values(4, 5, 100, 255)] byte badType)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        FuzzFrame frame = new()
        {
            Version = 0,
            Type = badType,
            Flags = 0,
            StreamId = 2,
            Length = 0,
            Payload = []
        };
        await session.FeedFramesAsync([frame], seed: 1);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, "Expected a GoAway for an unknown frame type.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        Assert.That(goAway.Header.StreamID, Is.Zero);
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_NonZeroVersion_ClosesSessionWithProtocolError(
        [Values(1, 2, 255)] byte badVersion)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync([new FuzzFrame
        {
            Version = badVersion,
            Type = (byte)YamuxHeaderType.Ping,
            Flags = (short)YamuxHeaderFlags.Syn,
            StreamId = 0,
            Length = 7,
            Payload = []
        }], seed: 2);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, "Expected a GoAway for a non-zero version.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await session.ShutdownAsync();
    }

    [TestCase((byte)YamuxHeaderType.Data, -1)]
    [TestCase((byte)YamuxHeaderType.Data, 262145)]
    [TestCase((byte)YamuxHeaderType.Data, 16777217)]
    [TestCase((byte)YamuxHeaderType.WindowUpdate, -1)]
    [TestCase((byte)YamuxHeaderType.WindowUpdate, int.MaxValue)]
    public async Task Fuzz_BadLengthPerType_ClosesSessionWithProtocolError(byte type, int length)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        // Open a stream first so the malformed frame targets a known stream.
        List<FuzzFrame> frames =
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = type,
                Flags = 0,
                StreamId = 2,
                Length = length,
                Payload = []
            }
        ];
        await session.FeedFramesAsync(frames, seed: 3);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, $"Expected a GoAway for type={type} length={length}.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_LargeWindowUpdate_WithoutIntOverflow_IsAccepted()
    {
        // A big delta that does not overflow the int counter only grants send credit;
        // it must not kill the session (only int overflow is a protocol error).
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.WindowUpdate,
                Flags = 0,
                StreamId = 2,
                Length = 16777217,
                Payload = []
            },
            FuzzFrame.PingSyn(length: 15)
        ], seed: 14);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 15, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive a non-overflowing window update.");
        Assert.That(session.FirstGoAway(outbound), Is.Null);
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_PingWithoutSyn_OnStreamZero_IsIgnored()
    {
        // Ping on stream 0 is legal with any flags; without SYN there is nothing to answer.
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.Ping,
                Flags = 0,
                StreamId = 0,
                Length = 1,
                Payload = []
            },
            FuzzFrame.PingSyn(length: 16)
        ], seed: 17);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 16, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive a non-SYN ping on stream 0.");
        Assert.That(session.FirstGoAway(outbound), Is.Null);
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_GoAwayOnStreamZero_EndsSessionWithOk(
        [Values(0, 1, 2, 99)] int code)
    {
        // Any GoAway on stream 0 (even with an unknown code) is a legal session
        // termination; the session answers with GoAway/Ok and closes the streams.
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.GoAway,
                Flags = 0,
                StreamId = 0,
                Length = code,
                Payload = []
            }
        ], seed: 18);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, "Session end must emit a GoAway.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.Ok));
        await session.ShutdownAsync();
    }

    [TestCase((byte)YamuxHeaderType.Data, 0)]
    [TestCase((byte)YamuxHeaderType.WindowUpdate, 0)]
    public async Task Fuzz_StreamZeroWithNonPingOrGoAway_ClosesSessionWithProtocolError(byte type, int streamId)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync([new FuzzFrame
        {
            Version = 0,
            Type = type,
            Flags = 0,
            StreamId = streamId,
            Length = type == (byte)YamuxHeaderType.Data ? 0 : 5,
            Payload = []
        }], seed: 4);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, $"Expected a GoAway for type={type} on stream 0.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_WrongSynParity_ClosesSessionWithProtocolError(
        [Values(true, false)] bool isListener)
    {
        using FuzzSession session = isListener ? FuzzSession.CreateListener() : FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        // Dialer side expects even stream ids for SYN, listener side expects odd ones.
        int badId = isListener ? 2 : 1;
        await session.FeedFramesAsync([FuzzFrame.Syn(streamId: badId)], seed: 5);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, "Expected a GoAway for SYN with wrong parity.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_UnknownFlagBits_AreIgnored(
        [Values((short)0x10, (short)0x20, (short)0x100, (short)-0x8000)] short unknownFlags)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.WindowUpdate,
                Flags = (short)((short)YamuxHeaderFlags.Syn | unknownFlags),
                StreamId = 2,
                Length = 0,
                Payload = []
            },
            FuzzFrame.PingSyn(length: 42)
        ], seed: 6);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 42, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive unknown flag bits and answer pings.");
        Assert.That(session.FirstGoAway(outbound), Is.Null, "Unknown flag bits must not produce a GoAway.");
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_DuplicateSyn_OnExistingStream_IsTolerated()
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            FuzzFrame.Syn(streamId: 2),
            FuzzFrame.PingSyn(length: 9)
        ], seed: 7);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 9, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive a duplicate SYN.");
        Assert.That(session.FirstGoAway(outbound), Is.Null, "Duplicate SYN must not produce a GoAway.");
        Assert.That(session.Upchannels, Has.Count.EqualTo(1), "Duplicate SYN must not open a second stream.");
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_FramesOnUnknownOrClosedStreams_AreIgnored()
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        byte[] data = [1, 2, 3, 4];
        await session.FeedFramesAsync(
        [
            // Unknown stream (never opened).
            new FuzzFrame { Version = 0, Type = (byte)YamuxHeaderType.Data, Flags = 0, StreamId = 8, Length = data.Length, Payload = data },
            new FuzzFrame { Version = 0, Type = (byte)YamuxHeaderType.WindowUpdate, Flags = 0, StreamId = 10, Length = 100, Payload = [] },
            new FuzzFrame { Version = 0, Type = (byte)YamuxHeaderType.WindowUpdate, Flags = (short)YamuxHeaderFlags.Fin, StreamId = 12, Length = 0, Payload = [] },
            new FuzzFrame { Version = 0, Type = (byte)YamuxHeaderType.WindowUpdate, Flags = (short)YamuxHeaderFlags.Rst, StreamId = 14, Length = 0, Payload = [] },
            FuzzFrame.PingSyn(length: 11)
        ], seed: 8);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 11, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive frames for unknown streams.");
        Assert.That(session.FirstGoAway(outbound), Is.Null);
        Assert.That(session.Upchannels, Is.Empty);
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_DataAboveReceiveWindow_ClosesSessionWithProtocolError()
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.Data,
                Flags = 0,
                StreamId = 2,
                Length = YamuxProtocol.ProtocolInitialWindowSize + 1,
                Payload = []
            }
        ], seed: 9);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, "Expected a GoAway for data above the receive window.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_WindowUpdateOverflow_ClosesSessionWithProtocolError(
        [Values(int.MaxValue, int.MaxValue - 10)] int delta)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.WindowUpdate,
                Flags = 0,
                StreamId = 2,
                Length = delta,
                Payload = []
            }
        ], seed: 10);
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        Assert.That(goAway, Is.Not.Null, "Expected a GoAway for an overflowing window update.");
        Assert.That(goAway!.Header.Length, Is.EqualTo((int)SessionTerminationCode.ProtocolError));
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_WindowUpdateAfterFinOrRst_DoesNotKillSession(
        [Values(4, 8)] short terminalFlag) // Fin = 4, Rst = 8
    {
        YamuxHeaderFlags terminal = (YamuxHeaderFlags)terminalFlag;
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.WindowUpdate,
                Flags = (short)terminal,
                StreamId = 2,
                Length = 0,
                Payload = []
            },
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.WindowUpdate,
                Flags = 0,
                StreamId = 2,
                Length = 100,
                Payload = []
            },
            FuzzFrame.PingSyn(length: 13)
        ], seed: 11);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 13, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive a window update after FIN/RST.");
        Assert.That(session.FirstGoAway(outbound), Is.Null);
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_PingOpaqueLengths_AreTolerated(
        [Values(0, 1, 999, int.MaxValue, -1, int.MinValue)] int opaque)
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.Ping,
                Flags = (short)YamuxHeaderFlags.Syn,
                StreamId = 0,
                Length = opaque,
                Payload = []
            }
        ], seed: 12);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, $"Ping with opaque length {opaque} must be acknowledged.");
        Assert.That(session.FirstGoAway(outbound), Is.Null);
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_GoAwayMidStream_ClosesStreamsAndEndsSession()
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync(
        [
            FuzzFrame.Syn(streamId: 2),
            FuzzFrame.Syn(streamId: 4),
            new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.GoAway,
                Flags = 0,
                StreamId = 0,
                Length = (int)SessionTerminationCode.Ok,
                Payload = []
            }
        ], seed: 13);
        await session.WaitForCompletionAsync();
        // The terminal GoAway/Ok is fire-and-forget: it usually lands, but the
        // channel may close first under load. Either way the session must end
        // cleanly with all streams closed.
        FuzzObservation? goAway = await session.WaitForGoAwayAsync(outbound, session.Timeout.Token);
        if (goAway is not null)
        {
            Assert.That(goAway.Header.Length, Is.EqualTo((int)SessionTerminationCode.Ok));
        }
        foreach (TestChannel up in session.Upchannels)
        {
            Assert.That((await up.ReadAsync(1, token: session.Timeout.Token)).Result,
                Is.Not.EqualTo(IOResult.Ok), "Open streams must be closed after GoAway.");
        }
    }

    [Test]
    public async Task Fuzz_CrossingSyn_SimultaneousOpen()
    {
        // Both ends open a stream at the same time over one transport pair.
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.Id.Returns("/test/1.0.0");

        (IConnectionContext dialerCtx, INewSessionContext dialerSession, List<TestChannel> dialerUps) =
            FuzzSession.MockDialer("dialer", [new UpgradeOptions { SelectedProtocol = protocol }]);
        (IConnectionContext listenerCtx, INewSessionContext listenerSession, List<TestChannel> listenerUps) =
            FuzzSession.MockListener();

        dialerSession.SubProtocols.Returns([protocol]);
        listenerSession.SubProtocols.Returns([protocol]);

        TestChannel transport = new();
        YamuxProtocol yamux = new();
        Task listen = yamux.ListenAsync(transport.Reverse(), listenerCtx);
        Task dial = yamux.DialAsync(transport, dialerCtx);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            Assert.That(dialerUps, Has.Count.EqualTo(1));
            // Listener side creates its upchannel once the dialer's SYN arrives.
            TestChannel? listenerUp = null;
            using CancellationTokenSource spin = new(TimeSpan.FromSeconds(5));
            while (!spin.IsCancellationRequested)
            {
                lock (listenerUps)
                {
                    if (listenerUps.Count > 0)
                    {
                        listenerUp = listenerUps[0];
                        break;
                    }
                }
                await Task.Delay(5, timeout.Token);
            }
            Assert.That(listenerUp, Is.Not.Null, "Listener must accept the dialer's stream.");

            // Exchange data in both directions at once.
            byte[] toListener = [10, 20, 30];
            byte[] toDialer = [40, 50];
            Assert.That(await dialerUps[0].Reverse().WriteAsync(new ReadOnlySequence<byte>(toListener), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            Assert.That(await listenerUp!.Reverse().WriteAsync(new ReadOnlySequence<byte>(toDialer), timeout.Token),
                Is.EqualTo(IOResult.Ok));
            Assert.That((await listenerUp.Reverse().ReadAsync(toListener.Length, token: timeout.Token).OrThrow()).ToArray(),
                Is.EqualTo(toListener));
            Assert.That((await dialerUps[0].Reverse().ReadAsync(toDialer.Length, token: timeout.Token).OrThrow()).ToArray(),
                Is.EqualTo(toDialer));
        }
        finally
        {
            await transport.CloseAsync();
            await Task.WhenAll(listen, dial).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Test]
    public async Task Fuzz_InterleavedPartialFrames_MultiStream()
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        Random rng = new(301);
        List<FuzzFrame> frames = [];
        for (int stream = 2; stream <= 8; stream += 2)
        {
            frames.Add(FuzzFrame.Syn(streamId: stream));
        }
        for (int i = 0; i < 40; i++)
        {
            int stream = 2 + 2 * rng.Next(4);
            byte[] payload = new byte[rng.Next(1, 64)];
            rng.NextBytes(payload);
            frames.Add(new FuzzFrame
            {
                Version = 0,
                Type = (byte)YamuxHeaderType.Data,
                Flags = 0,
                StreamId = stream,
                Length = payload.Length,
                Payload = payload
            });
        }
        frames.Add(FuzzFrame.PingSyn(length: 77));
        // One-byte chunks: every frame arrives maximally fragmented and interleaved.
        await session.FeedFramesAsync(frames, seed: 302, maxChunkSize: 1);
        FuzzObservation? pingAck = await session.WaitForPingAckAsync(outbound, opaque: 77, session.Timeout.Token);
        Assert.That(pingAck, Is.Not.Null, "Session must survive interleaved single-byte partial frames.");
        Assert.That(session.FirstGoAway(outbound), Is.Null);
        Assert.That(session.Upchannels, Has.Count.EqualTo(4));
        await session.ShutdownAsync();
    }

    [Test]
    public async Task Fuzz_TruncatedDataFrame_TerminatesWithoutFaultOrHang()
    {
        using FuzzSession session = FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        await session.FeedFramesAsync([FuzzFrame.Syn(streamId: 2)], seed: 401);
        // Header promises 100 payload bytes; deliver 10, then half-close the writer.
        FuzzFrame truncated = new()
        {
            Version = 0,
            Type = (byte)YamuxHeaderType.Data,
            Flags = 0,
            StreamId = 2,
            Length = 100,
            Payload = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]
        };
        await session.FeedBytesAsync(truncated.ToBytes(), seed: 402);
        await session.Remote.WriteEofAsync(session.Timeout.Token);
        // The read loop must terminate (no hang) and the protocol task must not fault.
        await session.WaitForCompletionAsync();
        _ = outbound;
    }

    [Test]
    public async Task Fuzz_CloseRace_DuringActiveTransfer()
    {
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.Id.Returns("/test/1.0.0");
        (IConnectionContext ctx, INewSessionContext sess, List<TestChannel> ups) =
            FuzzSession.MockDialer("race", [new UpgradeOptions { SelectedProtocol = protocol }]);
        sess.SubProtocols.Returns([protocol]);
        TestChannel appChannel = new();
        int upgrades = 0;
        sess.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            Interlocked.Increment(ref upgrades);
            return appChannel;
        });

        TestChannel transport = new();
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = YamuxHeader.FromBytes((await remote.ReadAsync(12, token: timeout.Token).OrThrow()).ToArray());
            // Push a burst of inbound data while racing transport close against writer activity.
            byte[] payload = new byte[4096];
            new Random(501).NextBytes(payload);
            List<Task> racers = [];
            for (int i = 0; i < 8; i++)
            {
                byte[] frame = new byte[12 + payload.Length];
                YamuxHeader data = new() { Type = YamuxHeaderType.Data, Length = payload.Length, StreamID = syn.StreamID };
                YamuxHeader.ToBytes(frame.AsSpan(0, 12), ref data);
                payload.CopyTo(frame, 12);
                racers.Add(remote.WriteAsync(new ReadOnlySequence<byte>(frame), timeout.Token).AsTask());
            }
            racers.Add(Task.Run(async () =>
            {
                await Task.Delay(10);
                await transport.CloseAsync();
            }));
            await Task.WhenAll(racers).WaitAsync(timeout.Token);
            // Must terminate promptly and never fault: no unhandled exception escapes.
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task RunMixedFrameFuzz(int seed, bool isListener)
    {
        using FuzzSession session = isListener ? FuzzSession.CreateListener() : FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        List<FuzzFrame> frames = FrameGenerator.MixedFrames(new Random(seed), isListener, count: 48);
        await session.FeedFramesAsync(frames, seed);
        // Let the session settle, then check the universal invariants.
        await Task.Delay(250, session.Timeout.Token);
        session.AssertUniversalInvariants(outbound, frames.Count);
        await session.ShutdownAsync();
    }

    private static async Task RunRawByteFuzz(int seed, bool isListener)
    {
        using FuzzSession session = isListener ? FuzzSession.CreateListener() : FuzzSession.CreateDialer();
        List<FuzzObservation> outbound = session.StartDraining();
        Random rng = new(seed);
        int total = rng.Next(64, 2048);
        byte[] blob = new byte[total];
        rng.NextBytes(blob);
        // 15% of the time start from a valid SYN so the fuzzer reaches stream code.
        if (seed % 7 == 0)
        {
            byte[] syn = FuzzFrame.Syn(streamId: isListener ? 1 : 2).ToBytes();
            syn.CopyTo(blob, 0);
        }
        await session.FeedBytesAsync(blob, seed);
        await Task.Delay(250, session.Timeout.Token);
        session.AssertUniversalInvariants(outbound, maxNewStreams: total / 12 + 1);
        await session.ShutdownAsync();
    }

    /// <summary>One fuzzed frame: raw header fields plus the payload bytes.</summary>
    internal sealed class FuzzFrame
    {
        public byte Version { get; init; }
        public byte Type { get; init; }
        public short Flags { get; init; }
        public int StreamId { get; init; }
        public int Length { get; init; }
        public byte[] Payload { get; init; } = [];

        public static FuzzFrame Syn(int streamId) => new()
        {
            Version = 0,
            Type = (byte)YamuxHeaderType.WindowUpdate,
            Flags = (short)YamuxHeaderFlags.Syn,
            StreamId = streamId,
            Length = 0,
            Payload = []
        };

        public static FuzzFrame PingSyn(int length) => new()
        {
            Version = 0,
            Type = (byte)YamuxHeaderType.Ping,
            Flags = (short)YamuxHeaderFlags.Syn,
            StreamId = 0,
            Length = length,
            Payload = []
        };

        public byte[] ToBytes()
        {
            byte[] header = new byte[12];
            header[0] = Version;
            header[1] = Type;
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(2), Flags);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), StreamId);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), Length);
            if (Payload.Length == 0)
            {
                return header;
            }
            byte[] full = new byte[header.Length + Payload.Length];
            header.CopyTo(full, 0);
            Payload.CopyTo(full, header.Length);
            return full;
        }
    }

    internal sealed record FuzzObservation(YamuxHeader Header, byte[] Payload);

    /// <summary>Deterministic frame-sequence generator covering the task's target list.</summary>
    internal static class FrameGenerator
    {
        private static readonly byte[] ValidTypes =
            [(byte)YamuxHeaderType.Data, (byte)YamuxHeaderType.WindowUpdate, (byte)YamuxHeaderType.Ping, (byte)YamuxHeaderType.GoAway];

        private static readonly int[] InterestingLengths =
            [0, 1, 2, 11, 255, 256, 1024, 262143, 262144, 262145, 1048576, 16777216, 16777217,
                -1, -2, int.MinValue, int.MaxValue, int.MaxValue - 1];

        public static List<FuzzFrame> MixedFrames(Random rng, bool isListener, int count)
        {
            List<FuzzFrame> frames = [];
            List<int> openStreams = [];
            int nextStreamId = isListener ? 1 : 2;
            int[] specialIds = [0, -1, -2, int.MinValue, int.MaxValue, int.MaxValue - 1];

            for (int i = 0; i < count; i++)
            {
                int pick = rng.Next(100);
                if ((pick < 25 && openStreams.Count < 6) || openStreams.Count == 0)
                {
                    // Open a new stream (usually with valid parity, sometimes not).
                    int id = nextStreamId;
                    nextStreamId += 2;
                    if (rng.Next(10) == 0)
                    {
                        id = rng.Next(2) == 0 ? id + 1 : -rng.Next(1, 100);
                    }
                    openStreams.Add(id);
                    frames.Add(new FuzzFrame
                    {
                        Version = PickVersion(rng),
                        Type = (byte)YamuxHeaderType.WindowUpdate,
                        Flags = (short)YamuxHeaderFlags.Syn,
                        StreamId = id,
                        Length = 0,
                        Payload = []
                    });
                    continue;
                }

                int stream = openStreams[rng.Next(openStreams.Count)];
                if (rng.Next(12) == 0)
                {
                    stream = specialIds[rng.Next(specialIds.Length)];
                }
                else if (rng.Next(12) == 0 && openStreams.Count > 0)
                {
                    // SYN on an existing stream.
                    frames.Add(new FuzzFrame
                    {
                        Version = 0,
                        Type = (byte)YamuxHeaderType.WindowUpdate,
                        Flags = (short)YamuxHeaderFlags.Syn,
                        StreamId = stream,
                        Length = 0,
                        Payload = []
                    });
                    continue;
                }

                byte type = PickType(rng);
                short flags = PickFlags(rng);
                int length = PickLength(rng, type);
                byte[] payload = [];
                if (type == (byte)YamuxHeaderType.Data && length is >= 0 and <= 4096)
                {
                    payload = new byte[length];
                    rng.NextBytes(payload);
                }
                frames.Add(new FuzzFrame
                {
                    Version = PickVersion(rng),
                    Type = type,
                    Flags = flags,
                    StreamId = stream,
                    Length = length,
                    Payload = payload
                });

                if (type == (byte)YamuxHeaderType.GoAway && stream == 0)
                {
                    break;
                }
            }
            return frames;
        }

        private static byte PickVersion(Random rng)
        {
            int roll = rng.Next(20);
            return roll < 17 ? (byte)0 : (byte)rng.Next(1, 256);
        }

        private static byte PickType(Random rng)
        {
            int roll = rng.Next(20);
            if (roll < 15)
            {
                return ValidTypes[rng.Next(ValidTypes.Length)];
            }
            return (byte)rng.Next(4, 256);
        }

        private static short PickFlags(Random rng)
        {
            int roll = rng.Next(20);
            if (roll < 12)
            {
                return (short)rng.Next(0, 16);
            }
            // Unknown flag bits, including the sign bit.
            return (short)rng.Next(short.MinValue, short.MaxValue + 1);
        }

        private static int PickLength(Random rng, byte type)
        {
            int roll = rng.Next(10);
            if (roll < 6)
            {
                return InterestingLengths[rng.Next(InterestingLengths.Length)];
            }
            if (roll < 8)
            {
                return rng.Next(-512, 4096);
            }
            int value = rng.Next();
            if (type == (byte)YamuxHeaderType.Data && rng.Next(2) == 0)
            {
                // Keep most data payloads deliverable so the fuzzer reaches stream code.
                return value % 1024;
            }
            return rng.Next(2) == 0 ? value : -value;
        }
    }

    /// <summary>A single live yamux session under test with helpers to feed and observe it.</summary>
    internal sealed class FuzzSession : IDisposable
    {
        private readonly TestChannel _transport;
        private readonly Task _protocolTask;
        private readonly CancellationTokenSource _drainCts = new();
        private readonly List<TestChannel> _upchannels;

        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(5));
        public IChannel Remote { get; }
        public int FramesFed { get; private set; }

        /// <summary>Upchannels created via the mocked session (live list, lock before iterating).</summary>
        public List<TestChannel> Upchannels => _upchannels;

        private FuzzSession(TestChannel transport, IChannel remote, Task protocolTask, List<TestChannel> upchannels)
        {
            _transport = transport;
            Remote = remote;
            _protocolTask = protocolTask;
            _upchannels = upchannels;
        }

        public static FuzzSession CreateDialer(IEnumerable<UpgradeOptions>? dialRequests = null)
        {
            (IConnectionContext ctx, INewSessionContext _, List<TestChannel> ups) =
                MockDialer("fuzz", dialRequests ?? []);
            TestChannel transport = new();
            Task task = new YamuxProtocol().DialAsync(transport, ctx);
            return new FuzzSession(transport, transport.Reverse(), task, ups);
        }

        public static FuzzSession CreateListener()
        {
            (_, IConnectionContext ctx, List<TestChannel> ups) = MockListenerSetup();
            TestChannel transport = new();
            Task task = new YamuxProtocol().ListenAsync(transport, ctx);
            return new FuzzSession(transport, transport.Reverse(), task, ups);
        }

        public static (IConnectionContext Ctx, INewSessionContext Session, List<TestChannel> Ups) MockDialer(
            string id, IEnumerable<UpgradeOptions> dialRequests)
        {
            IProtocol protocol = Substitute.For<IProtocol>();
            protocol.Id.Returns("/test/1.0.0");
            IConnectionContext context = Substitute.For<IConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            context.UpgradeToSession().Returns(session);
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
            session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(2) });
            session.Id.Returns(id);
            session.DialRequests.Returns(dialRequests);
            session.SubProtocols.Returns([protocol]);
            List<TestChannel> ups = [];
            session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                TestChannel up = new();
                lock (ups)
                {
                    ups.Add(up);
                }
                return up;
            });
            return (context, session, ups);
        }

        public static (INewSessionContext Session, IConnectionContext Ctx, List<TestChannel> Ups) MockListenerSetup()
        {
            IProtocol protocol = Substitute.For<IProtocol>();
            protocol.Id.Returns("/test/1.0.0");
            IConnectionContext context = Substitute.For<IConnectionContext>();
            INewSessionContext session = Substitute.For<INewSessionContext>();
            context.UpgradeToSession().Returns(session);
            context.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
            session.State.Returns(new State { RemoteAddress = TestPeers.Multiaddr(1) });
            session.Id.Returns("fuzz-listener");
            session.SubProtocols.Returns([protocol]);
            List<TestChannel> ups = [];
            session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
            {
                TestChannel up = new();
                lock (ups)
                {
                    ups.Add(up);
                }
                return up;
            });
            return (session, context, ups);
        }

        public static (IConnectionContext Ctx, INewSessionContext Session, List<TestChannel> Ups) MockListener()
        {
            (INewSessionContext s, IConnectionContext c, List<TestChannel> u) = MockListenerSetup();
            return (c, s, u);
        }

        /// <summary>Reads every outbound frame in the background so yamux writers never block.</summary>
        public List<FuzzObservation> StartDraining()
        {
            List<FuzzObservation> outbound = [];
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_drainCts.IsCancellationRequested)
                    {
                        ReadResult headerRead = await Remote.ReadAsync(12, token: _drainCts.Token);
                        if (headerRead.Result != IOResult.Ok)
                        {
                            break;
                        }
                        YamuxHeader header = YamuxHeader.FromBytes(headerRead.Data.ToArray());
                        byte[] payload = [];
                        if (header.Type == YamuxHeaderType.Data && header.Length > 0 && header.Length <= 64 * 1024 * 1024)
                        {
                            ReadResult payloadRead = await Remote.ReadAsync(header.Length, token: _drainCts.Token);
                            if (payloadRead.Result == IOResult.Ok)
                            {
                                payload = payloadRead.Data.ToArray();
                            }
                        }
                        lock (outbound)
                        {
                            outbound.Add(new FuzzObservation(header, payload));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            return outbound;
        }

        public async Task FeedFramesAsync(IReadOnlyList<FuzzFrame> frames, int seed, int? maxChunkSize = null)
        {
            Random rng = new(seed * 7919 + 13);
            foreach (FuzzFrame frame in frames)
            {
                await FeedBytesAsync(frame.ToBytes(), rng, maxChunkSize);
                FramesFed++;
            }
        }

        public Task FeedBytesAsync(byte[] blob, int seed) => FeedBytesAsync(blob, new Random(seed * 104729 + 7), null);

        private async Task FeedBytesAsync(byte[] blob, Random rng, int? maxChunkSize)
        {
            int offset = 0;
            while (offset < blob.Length)
            {
                int limit = maxChunkSize ?? rng.Next(1, 25);
                int size = Math.Min(limit, blob.Length - offset);
                // Occasionally align to the 12-byte header to hit the decode path cleanly.
                if (maxChunkSize is null && offset % 12 == 0 && rng.Next(3) == 0)
                {
                    size = Math.Min(12, blob.Length - offset);
                }
                byte[] chunk = new byte[size];
                blob.AsSpan(offset, size).CopyTo(chunk);
                IOResult written = await Remote.WriteAsync(new ReadOnlySequence<byte>(chunk), Timeout.Token);
                if (written != IOResult.Ok)
                {
                    break;
                }
                offset += size;
            }
        }

        public FuzzObservation? FirstGoAway(List<FuzzObservation> outbound)
        {
            lock (outbound)
            {
                return outbound.FirstOrDefault(o => o.Header.Type == YamuxHeaderType.GoAway);
            }
        }

        public async Task<FuzzObservation?> WaitForGoAwayAsync(List<FuzzObservation> outbound, CancellationToken token)
        {
            for (int i = 0; i < 100; i++)
            {
                FuzzObservation? goAway = FirstGoAway(outbound);
                if (goAway is not null)
                {
                    return goAway;
                }
                if (_protocolTask.IsCompleted)
                {
                    break;
                }
                await Task.Delay(50, token);
            }
            return FirstGoAway(outbound);
        }

        public async Task<FuzzObservation?> WaitForPingAckAsync(List<FuzzObservation> outbound, int opaque, CancellationToken token)
        {
            for (int i = 0; i < 100; i++)
            {
                lock (outbound)
                {
                    FuzzObservation? ack = outbound.FirstOrDefault(o =>
                        o.Header.Type == YamuxHeaderType.Ping &&
                        (o.Header.Flags & YamuxHeaderFlags.Ack) != 0 &&
                        o.Header.Length == opaque);
                    if (ack is not null)
                    {
                        return ack;
                    }
                }
                await Task.Delay(50, token);
            }
            return null;
        }

        /// <summary>
        /// Universal invariants for every fuzz run: no fault, no hang, bounded
        /// streams, and only legal GoAway codes on the wire.
        /// </summary>
        public void AssertUniversalInvariants(List<FuzzObservation> outbound, int maxNewStreams)
        {
            Assert.That(_protocolTask.IsFaulted, Is.False,
                $"Unhandled exception escaped the protocol: {_protocolTask.Exception?.GetBaseException().Message}");
            List<FuzzObservation> snapshot;
            lock (outbound)
            {
                snapshot = [.. outbound];
            }
            foreach (FuzzObservation obs in snapshot.Where(o => o.Header.Type == YamuxHeaderType.GoAway))
            {
                Assert.That(obs.Header.StreamID, Is.Zero, "GoAway must use stream 0.");
                Assert.That(obs.Header.Length, Is.AnyOf(
                    (int)SessionTerminationCode.Ok,
                    (int)SessionTerminationCode.ProtocolError,
                    (int)SessionTerminationCode.InternalError),
                    $"Illegal GoAway code {obs.Header.Length}.");
            }
            lock (Upchannels)
            {
                Assert.That(Upchannels.Count, Is.LessThanOrEqualTo(maxNewStreams + 1),
                    $"Stream count blew past the bound: {Upchannels.Count} upchannels.");
            }
        }

        public async Task ShutdownAsync()
        {
            await _transport.CloseAsync();
            await WaitForCompletionAsync();
        }

        public async Task WaitForCompletionAsync()
        {
            // No exchange may hang: the protocol task must finish promptly after close.
            await _protocolTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(_protocolTask.IsFaulted, Is.False,
                $"Unhandled exception escaped the protocol: {_protocolTask.Exception?.GetBaseException().Message}");
        }

        public void Dispose()
        {
            _drainCts.Cancel();
            Timeout.Dispose();
            _drainCts.Dispose();
        }
    }
}
