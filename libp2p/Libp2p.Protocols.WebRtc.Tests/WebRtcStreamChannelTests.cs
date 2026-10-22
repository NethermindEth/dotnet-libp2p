// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Buffers;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
// A stalled read or a never-released writer must turn the run red rather than wedge it: an earlier
// session lost the whole runner to a read awaited without a token.
[CancelAfter(15000)]
public class WebRtcStreamChannelTests
{
    /// <summary>Only guards against a hang: every test here reaches its result without waiting.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task WaitAll_AssemblesOneReadFromSeveralFrames()
    {
        // Frame boundaries are a framing artifact the reader never chose, so an exact-length read
        // has to span them.
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream sender = new(channelA);
        await using WebRtcStream receiver = new(channelB);
        WebRtcStreamChannel channel = new(receiver);

        byte[] payload = new byte[100];
        new Random(7).NextBytes(payload);
        foreach (Range part in new[] { 0..30, 30..61, 61..100 })
        {
            await sender.WriteAsync(payload[part]);
        }

        Assert.That(channelA.SentFrames, Has.Count.EqualTo(3));

        using CancellationTokenSource cts = new(TestTimeout);
        ReadResult read = await channel.ReadAsync(100, ReadBlockingMode.WaitAll, cts.Token);

        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(read.Data.ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public async Task CloseAsync_SendsExactlyOneFin()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream local = new(channelA);
        await using WebRtcStream remote = new(channelB);
        WebRtcStreamChannel channel = new(local);

        await channel.CloseAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(channelA.SentFrames, Has.Count.EqualTo(1));
        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
    }

    [Test]
    public async Task WriteEofThenClose_StillSendsOneFin()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream local = new(channelA);
        await using WebRtcStream remote = new(channelB);
        WebRtcStreamChannel channel = new(local);

        Assert.That(await channel.WriteEofAsync().AsTask().WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));
        await channel.CloseAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
    }

    [Test]
    public async Task CloseReadAsync_SendsStopSending()
    {
        (FakeWebRtcDataChannel channelA, _) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream stream = new(channelA);
        WebRtcStreamChannel channel = new(stream);

        await channel.CloseReadAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.StopSending }));
    }

    [Test]
    public async Task AbortAsync_SendsResetStream()
    {
        (FakeWebRtcDataChannel channelA, _) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream stream = new(channelA);
        WebRtcStreamChannel channel = new(stream);

        await channel.AbortAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.ResetStream }));
    }

    [Test]
    public async Task PeerReset_SurfacesAsAbortedAndNeverAsEnded()
    {
        // Reporting a reset as a clean end is silent truncation: the reader would believe the
        // message it holds is the whole message.
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream stream = new(channelB);
        WebRtcStreamChannel channel = new(stream);

        await channelA.SendAsync(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null));

        using CancellationTokenSource cts = new(TestTimeout);
        ReadResult read = await channel.ReadAsync(1, ReadBlockingMode.WaitAll, cts.Token);

        Assert.That(read.Result, Is.Not.EqualTo(IOResult.Ended));
        Assert.That(read.Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task PeerFin_EndsTheReadCleanly()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcStream stream = new(channelB);
        WebRtcStreamChannel channel = new(stream);

        await channelA.SendAsync(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));

        using CancellationTokenSource cts = new(TestTimeout);
        ReadResult read = await channel.ReadAsync(1, ReadBlockingMode.WaitAll, cts.Token);

        Assert.That(read.Result, Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task WriteAsync_ParksOnBackpressureAndCompletesAfterDrain()
    {
        (FakeWebRtcDataChannel channelA, _) = FakeWebRtcDataChannel.CreatePair();
        channelA.SetBufferedAmount(WebRtcStream.MaxBufferedAmount);
        await using WebRtcStream stream = new(channelA);
        WebRtcStreamChannel channel = new(stream);

        Task<IOResult> write = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 })).AsTask();

        Assert.That(write.IsCompleted, Is.False);
        Assert.That(channelA.SentFrames, Is.Empty);

        channelA.Drain();

        Assert.That(await write.WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));
        Assert.That(channelA.SentFrames, Has.Count.EqualTo(1));
    }

    private static Message.Types.Flag[] Flags(FakeWebRtcDataChannel channel) =>
        [.. channel.SentFrames.Select(Decode).Where(message => message.HasFlag).Select(message => message.Flag)];

    private static Message Decode(byte[] frame)
    {
        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out _), Is.True);
        return message!;
    }
}
