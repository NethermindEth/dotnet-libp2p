// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using NSubstitute;
using static Nethermind.Libp2p.Protocols.Yamux.Tests.YamuxFaultInjectionTests;

namespace Nethermind.Libp2p.Protocols.Yamux.Tests;

/// <summary>
/// Wire-level conformance against the specification and the go-yamux and
/// rust-yamux reference implementations, driven by a raw frame peer:
/// bounded outbound DATA frames, the window advertised in SYN/ACK, FIN carried
/// on DATA, zero-length DATA and headers split across transport reads.
/// </summary>
[TestFixture]
public class YamuxWireConformanceTests
{
    [Test]
    public async Task OutboundDataFramesNeverExceedMaxFrameSize()
    {
        (IConnectionContext ctx, List<Channel> ups) = Dialer();
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().DialAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task drain = DrainAsync(transport.Reverse(), outbound, timeout.Token);
        try
        {
            IChannel remote = transport.Reverse();
            YamuxHeader syn = await WaitForOutboundSynAsync(outbound, timeout.Token);
            // Grant far more credit than one frame may carry.
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.WindowUpdate,
                Flags = YamuxHeaderFlags.Ack,
                StreamID = syn.StreamID,
                Length = 4 * 1024 * 1024
            }, timeout.Token);

            IChannel application = (await WaitForUpchannelAsync(ups, timeout.Token)).Reverse;
            byte[] payload = new byte[2 * 1024 * 1024];
            new Random(7).NextBytes(payload);
            Assert.That(await application.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token), Is.EqualTo(IOResult.Ok));

            FuzzObservation[] data = await WaitForDataAsync(outbound, syn.StreamID, payload.Length, timeout.Token);
            Assert.Multiple(() =>
            {
                // rust-yamux rejects DATA bodies above 1 MiB (MAX_FRAME_BODY_LEN); go-yamux splits at 64 KiB messages.
                Assert.That(YamuxProtocol.MaxDataFrameLength, Is.LessThanOrEqualTo(1024 * 1024));
                Assert.That(data.Select(o => o.Header.Length), Is.All.InRange(1, YamuxProtocol.MaxDataFrameLength),
                    "A single DATA frame must not carry the whole remote window.");
                Assert.That(data.Max(o => o.Header.Length), Is.EqualTo(YamuxProtocol.MaxDataFrameLength), "Writes are split exactly at the limit.");
                Assert.That(data.SelectMany(o => o.Payload).ToArray(), Is.EqualTo(payload), "Segmentation must preserve the byte stream.");
            });
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
        await drain;
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task SynAndAckAdvertiseReceiveWindowAboveProtocolDefault(bool isListener, bool enlarged)
    {
        const int enlargedWindow = 1024 * 1024;
        YamuxProtocol yamux = enlarged
            ? new(windowSettings: new YamuxWindowSettings { InitialWindowSize = enlargedWindow })
            : new();
        TestChannel transport = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task session = isListener
            ? yamux.ListenAsync(transport, Listener().Ctx)
            : yamux.DialAsync(transport, Dialer().Ctx);
        try
        {
            IChannel remote = transport.Reverse();
            if (isListener)
            {
                await WriteHeaderAsync(remote, new YamuxHeader
                {
                    Type = YamuxHeaderType.WindowUpdate,
                    Flags = YamuxHeaderFlags.Syn,
                    StreamID = 1
                }, timeout.Token);
            }
            YamuxHeader opening = await ReadOutboundHeaderAsync(remote, timeout.Token);
            Assert.Multiple(() =>
            {
                Assert.That(opening.Type, Is.EqualTo(YamuxHeaderType.WindowUpdate));
                Assert.That(opening.Flags, Is.EqualTo(isListener ? YamuxHeaderFlags.Ack : YamuxHeaderFlags.Syn));
                Assert.That(opening.StreamID, Is.EqualTo(1));
                Assert.That(opening.Length, Is.EqualTo(enlarged ? enlargedWindow - YamuxProtocol.ProtocolInitialWindowSize : 0),
                    "SYN/ACK carries only the delta above the 256 KiB the peer already assumes.");
            });
        }
        finally
        {
            await transport.CloseAsync();
            await session.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(session.IsFaulted, Is.False);
        }
    }

    [Test]
    public async Task DataWithFinDeliversPayloadThenEofAndKeepsOutboundOpen()
    {
        (IConnectionContext ctx, List<Channel> ups) = Listener();
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().ListenAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task drain = DrainAsync(transport.Reverse(), outbound, timeout.Token);
        try
        {
            IChannel remote = transport.Reverse();
            byte[] request = [10, 20, 30, 40, 50];
            await WriteHeaderAsync(remote, new YamuxHeader
            {
                Type = YamuxHeaderType.Data,
                Flags = YamuxHeaderFlags.Syn | YamuxHeaderFlags.Fin,
                StreamID = 1,
                Length = request.Length
            }, timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(request), timeout.Token), Is.EqualTo(IOResult.Ok));

            IChannel application = (await WaitForUpchannelAsync(ups, timeout.Token)).Reverse;
            Assert.That((await application.ReadAsync(request.Length, token: timeout.Token).OrThrow()).ToArray(), Is.EqualTo(request));
            Assert.That((await application.ReadAsync(1, token: timeout.Token)).Result, Is.EqualTo(IOResult.Ended),
                "FIN on a DATA frame half-closes the stream after its payload.");

            byte[] reply = [7, 8, 9];
            Assert.That(await application.WriteAsync(new ReadOnlySequence<byte>(reply), timeout.Token), Is.EqualTo(IOResult.Ok));
            Assert.That(await application.WriteEofAsync(timeout.Token), Is.EqualTo(IOResult.Ok));
            FuzzObservation[] data = await WaitForDataAsync(outbound, 1, reply.Length, timeout.Token);
            Assert.That(data.SelectMany(o => o.Payload).ToArray(), Is.EqualTo(reply), "The outbound half stays open after the peer's FIN.");
            Assert.That(await WaitForFlagAsync(outbound, YamuxHeaderFlags.Fin, 1, timeout.Token), Is.Not.Null);
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
    public async Task ZeroLengthDataFrameDeliversNothingAndKeepsStreamOpen()
    {
        (IConnectionContext ctx, List<Channel> ups) = Listener();
        TestChannel transport = new();
        List<FuzzObservation> outbound = [];
        Task yamux = new YamuxProtocol().ListenAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        Task drain = DrainAsync(transport.Reverse(), outbound, timeout.Token);
        try
        {
            IChannel remote = transport.Reverse();
            await WriteHeaderAsync(remote, new YamuxHeader { Type = YamuxHeaderType.WindowUpdate, Flags = YamuxHeaderFlags.Syn, StreamID = 1 }, timeout.Token);
            await WriteHeaderAsync(remote, new YamuxHeader { Type = YamuxHeaderType.Data, StreamID = 1, Length = 0 }, timeout.Token);
            byte[] payload = [1, 2, 3];
            await WriteHeaderAsync(remote, new YamuxHeader { Type = YamuxHeaderType.Data, StreamID = 1, Length = payload.Length }, timeout.Token);
            Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token), Is.EqualTo(IOResult.Ok));

            IChannel application = (await WaitForUpchannelAsync(ups, timeout.Token)).Reverse;
            Assert.That((await application.ReadAsync(0, ReadBlockingMode.WaitAny, timeout.Token).OrThrow()).ToArray(), Is.EqualTo(payload),
                "The empty frame contributes no bytes and does not end the stream.");
            await WriteHeaderAsync(remote, FuzzPingSyn(41), timeout.Token);
            Assert.That(await WaitForPingAckAsync(outbound, 41, timeout.Token), Is.Not.Null);
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
    public async Task FramesSplitAcrossTransportReadsAreDeliveredExactlyPerStream()
    {
        (IConnectionContext ctx, List<Channel> ups) = Listener();
        TestChannel transport = new();
        Task yamux = new YamuxProtocol().ListenAsync(transport, ctx);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            IChannel remote = transport.Reverse();
            int[] streams = [1, 3, 5];
            Random rng = new(11);
            byte[][] expected = streams.Select(_ => new byte[3000]).ToArray();
            foreach (byte[] bytes in expected)
            {
                rng.NextBytes(bytes);
            }

            // SYN for every stream, then DATA chunks interleaved across streams; the whole
            // byte sequence hits the transport one byte at a time, so every header and
            // payload straddles transport reads.
            using MemoryStream wire = new();
            foreach (int id in streams)
            {
                AppendFrame(wire, new YamuxHeader { Type = YamuxHeaderType.WindowUpdate, Flags = YamuxHeaderFlags.Syn, StreamID = id }, []);
            }
            for (int offset = 0; offset < 3000; offset += 500)
            {
                for (int i = 0; i < streams.Length; i++)
                {
                    AppendFrame(wire, new YamuxHeader { Type = YamuxHeaderType.Data, StreamID = streams[i], Length = 500 },
                        expected[i].AsSpan(offset, 500).ToArray());
                }
            }
            foreach (int id in streams)
            {
                AppendFrame(wire, new YamuxHeader { Type = YamuxHeaderType.WindowUpdate, Flags = YamuxHeaderFlags.Fin, StreamID = id }, []);
            }

            byte[] all = wire.ToArray();
            Task<IOResult[]> feed = Task.Run(async () =>
            {
                IOResult[] results = new IOResult[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    results[i] = await remote.WriteAsync(new ReadOnlySequence<byte>(all, i, 1), timeout.Token);
                }
                return results;
            });

            for (int i = 0; i < streams.Length; i++)
            {
                IChannel application = (await WaitForUpchannelAsync(ups, timeout.Token, i)).Reverse;
                using MemoryStream received = new();
                await foreach (ReadOnlySequence<byte> chunk in application.ReadAllAsync(timeout.Token))
                {
                    foreach (ReadOnlyMemory<byte> segment in chunk)
                    {
                        received.Write(segment.Span);
                    }
                }
                Assert.That(received.ToArray(), Is.EqualTo(expected[i]), $"Stream {streams[i]} bytes");
            }
            Assert.That(await feed.WaitAsync(timeout.Token), Is.All.EqualTo(IOResult.Ok));
        }
        finally
        {
            await transport.CloseAsync();
            await yamux.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(yamux.IsFaulted, Is.False);
        }
    }

    private static void AppendFrame(MemoryStream wire, YamuxHeader header, byte[] payload)
    {
        byte[] frame = new byte[12];
        YamuxHeader.ToBytes(frame, ref header);
        wire.Write(frame);
        wire.Write(payload);
    }

    private static (IConnectionContext Ctx, List<Channel> Ups) Dialer()
    {
        (IConnectionContext ctx, INewSessionContext session) = Mocks.Dialer([new UpgradeOptions { SelectedProtocol = Mocks.TestProtocol() }]);
        return (ctx, CollectUpChannels(session));
    }

    private static (IConnectionContext Ctx, List<Channel> Ups) Listener()
    {
        (IConnectionContext ctx, INewSessionContext session) = Mocks.Listener();
        return (ctx, CollectUpChannels(session));
    }

    private static List<Channel> CollectUpChannels(INewSessionContext session)
    {
        List<Channel> ups = [];
        session.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            Channel up = new();
            lock (ups) { ups.Add(up); }
            return up;
        });
        return ups;
    }

    private static async Task<Channel> WaitForUpchannelAsync(List<Channel> ups, CancellationToken token, int index = 0)
    {
        for (int i = 0; i < 200; i++)
        {
            lock (ups)
            {
                if (ups.Count > index)
                {
                    return ups[index];
                }
            }
            await Task.Delay(25, token);
        }
        throw new TimeoutException("Upchannel was not created.");
    }

    private static async Task<YamuxHeader> WaitForOutboundSynAsync(List<FuzzObservation> outbound, CancellationToken token)
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

    private static async Task<FuzzObservation[]> WaitForDataAsync(
        List<FuzzObservation> outbound, int streamId, long expectedBytes, CancellationToken token)
    {
        while (true)
        {
            FuzzObservation[] data;
            lock (outbound)
            {
                data = outbound.Where(o => o.Header.Type == YamuxHeaderType.Data && o.Header.StreamID == streamId).ToArray();
            }
            if (data.Sum(o => (long)o.Payload.Length) >= expectedBytes)
            {
                return data;
            }
            await Task.Delay(25, token);
        }
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
}
