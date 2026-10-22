// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
// A stalled state machine must turn the run red rather than wedge it: several of these tests
// previously hung the runner instead of failing when the behaviour they name regressed.
[CancelAfter(15000)]
public class WebRtcStreamTests
{
    /// <summary>Only guards against a hang: every test here reaches its result without waiting.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RoundTrips_SmallPayloadInBothDirections()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        WebRtcStream b = new(channelB);

        await a.WriteAsync(new byte[] { 1, 2, 3 });
        Assert.That(await b.ReadAsync(), Is.EqualTo(new byte[] { 1, 2, 3 }));

        await b.WriteAsync(new byte[] { 4, 5 });
        Assert.That(await a.ReadAsync(), Is.EqualTo(new byte[] { 4, 5 }));
    }

    [Test]
    public async Task LargeWrite_IsChunkedIntoFramesWithinTheLimit()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        WebRtcStream b = new(channelB);

        byte[] payload = new byte[WebRtcFraming.MaxPayloadSize(false) * 3 + 123];
        new Random(1234).NextBytes(payload);

        await a.WriteAsync(payload);

        List<byte> received = [];
        while (received.Count < payload.Length)
        {
            received.AddRange((await b.ReadAsync())!);
        }

        Assert.That(received, Is.EqualTo(payload));
        Assert.That(channelA.SentFrames, Has.Count.EqualTo(4));
        Assert.That(channelA.SentFrames.Select(frame => frame.Length), Has.All.LessThanOrEqualTo(WebRtcFraming.MaxFrameSize));
    }

    [Test]
    public async Task TwoFramesInOneMessage_AreBothDecoded()
    {
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        byte[] first = WebRtcFraming.Encode(null, new byte[] { 1 });
        byte[] second = WebRtcFraming.Encode(null, new byte[] { 2, 3 });
        channel.Deliver([.. first, .. second]);

        Assert.That(await stream.ReadAsync(), Is.EqualTo(new byte[] { 1 }));
        Assert.That(await stream.ReadAsync(), Is.EqualTo(new byte[] { 2, 3 }));
    }

    [Test]
    public async Task FrameSplitAcrossTwoMessages_IsDecoded()
    {
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        byte[] frame = WebRtcFraming.Encode(null, new byte[] { 1, 2, 3, 4, 5 });
        channel.Deliver(frame[..3]);
        channel.Deliver(frame[3..]);

        Assert.That(await stream.ReadAsync(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public async Task CloseWrites_CompletesOnFinAck_AndLeavesTheReadHalfOpen()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        WebRtcStream b = new(channelB);

        await a.CloseWritesAsync().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
        Assert.That(await b.ReadAsync(), Is.Null);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await a.WriteAsync(new byte[] { 1 }));

        await b.WriteAsync(new byte[] { 9 });
        Assert.That(await a.ReadAsync(), Is.EqualTo(new byte[] { 9 }));
    }

    [Test]
    public async Task CloseWrites_IsIdempotent()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        _ = new WebRtcStream(channelB);

        await a.CloseWritesAsync().WaitAsync(TestTimeout);
        Task second = a.CloseWritesAsync();

        Assert.That(second.IsCompleted, Is.True);
        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
    }

    [Test]
    public async Task CloseWrites_GivesUpWhenNoFinAckArrives()
    {
        // No peer is attached, so nothing will ever acknowledge the FIN.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel, TimeSpan.FromMilliseconds(50));

        await stream.CloseWritesAsync().WaitAsync(TestTimeout);

        Assert.That(Flags(channel), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
    }

    [Test]
    public async Task StopSending_StopsPeerWrites_AndIsNotAnsweredWithResetStream()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        WebRtcStream b = new(channelB);

        await b.StopReadingAsync();

        // A peer-driven condition, so it must not share a type with the caller's own misuse of
        // writing after CloseWritesAsync.
        Assert.ThrowsAsync<IOException>(async () => await a.WriteAsync(new byte[] { 1 }));
        Assert.That(channelA.SentFrames, Is.Empty);
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.StopSending }));
        Assert.That(await b.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task WriteAfterCloseWrites_IsCallerMisuse_NotAnIoFailure()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        _ = new WebRtcStream(channelB);

        await a.CloseWritesAsync().WaitAsync(TestTimeout);

        // Distinct from the IOException a remote STOP_SENDING produces: this one is the caller's
        // own bug, and a caller should be able to tell them apart.
        Assert.ThrowsAsync<InvalidOperationException>(async () => await a.WriteAsync(new byte[] { 1 }));
    }

    [Test]
    public async Task StopSending_LeavesTheSenderActingOnInboundFlags()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        _ = new WebRtcStream(channelA);
        WebRtcStream b = new(channelB);

        await b.StopReadingAsync();

        // Only completes if B still processes the FIN_ACK that answers its FIN.
        await b.CloseWritesAsync().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
    }

    [Test]
    public async Task Reset_FailsBothHalvesOnBothSides()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        WebRtcStream b = new(channelB);

        await a.WriteAsync(new byte[] { 1 });
        await a.ResetAsync();

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.ResetStream }));
        Assert.ThrowsAsync<IOException>(async () => await a.ReadAsync());
        Assert.ThrowsAsync<IOException>(async () => await a.WriteAsync(new byte[] { 2 }));
        Assert.ThrowsAsync<IOException>(async () => await b.ReadAsync());
        Assert.ThrowsAsync<IOException>(async () => await b.WriteAsync(new byte[] { 2 }));
    }

    [Test]
    public void MalformedFrame_SurfacesAsAStreamFailure()
    {
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        // Length prefix of 32767, past the 16kiB a framed message may occupy.
        Assert.DoesNotThrow(() => channel.Deliver([0xFF, 0xFF, 0x01]));

        Assert.ThrowsAsync<InvalidDataException>(async () => await stream.ReadAsync());
        Assert.ThrowsAsync<InvalidDataException>(async () => await stream.WriteAsync(new byte[] { 1 }));
        Assert.That(channel.IsClosed, Is.True);
    }

    [Test]
    public async Task Backpressure_HoldsTheWriteUntilTheChannelDrains()
    {
        FakeWebRtcDataChannel channel = new();
        channel.SetBufferedAmount(WebRtcStream.MaxBufferedAmount);
        WebRtcStream stream = new(channel);

        Task write = stream.WriteAsync(new byte[WebRtcFraming.MaxPayloadSize(false) * 2]).AsTask();

        Assert.That(write.IsCompleted, Is.False);
        Assert.That(channel.SentFrames, Is.Empty);

        channel.Drain();

        await write.WaitAsync(TestTimeout);
        Assert.That(channel.SentFrames, Has.Count.EqualTo(2));
    }

    private static Message.Types.Flag[] Flags(FakeWebRtcDataChannel channel) =>
        [.. channel.SentFrames.Select(Decode).Where(message => message.HasFlag).Select(message => message.Flag)];

    private static Message Decode(byte[] frame)
    {
        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out _), Is.True);
        return message!;
    }

    /// <summary>Bounded poll for work completing on a detached task; only slows a failing run.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(1);
        }

        Assert.That(condition(), Is.True, "condition never became true");
    }

    [Test]
    public async Task CloseWrites_StaysPendingUntilFinAckArrives()
    {
        // "A node SHOULD only consider its write-half closed once it has received a FIN_ACK."
        // Without this the FIN_ACK wait can be deleted outright and every other test still passes.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        Task close = stream.CloseWritesAsync();
        await WaitUntil(() => Flags(channel).Contains(Message.Types.Flag.Fin));
        Assert.That(close.IsCompleted, Is.False, "write half closed before any FIN_ACK arrived");

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        await close.WaitAsync(TestTimeout);
    }

    [Test]
    public async Task CleanFinExchange_ClosesTheDataChannel()
    {
        // "When a FIN_ACK and a FIN have been received, the node may close the datachannel."
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        WebRtcStream a = new(channelA);
        WebRtcStream b = new(channelB);

        await a.CloseWritesAsync().WaitAsync(TestTimeout);
        await b.CloseWritesAsync().WaitAsync(TestTimeout);

        await WaitUntil(() => channelA.IsClosed);
        Assert.That(channelA.IsClosed, Is.True);
    }

    [Test]
    public async Task PayloadArrivingAfterFin_IsNotDelivered()
    {
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));
        channel.Deliver(WebRtcFraming.Encode(null, new byte[] { 9 }));

        Assert.That(await stream.ReadAsync(), Is.Null, "payload after FIN must not reach the reader");

        // The read half being complete already drops the payload, so that assertion alone cannot
        // see the guard disappear. What the guard prevents is treating the stray frame as an
        // overflow and failing a stream whose write half is still perfectly usable.
        Assert.DoesNotThrowAsync(async () => await stream.WriteAsync(new byte[] { 1 }),
            "a stray post-FIN payload must not fail the still-open write half");
    }

    [Test]
    public void InboundOverflow_FailsTheStream()
    {
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        for (int i = 0; i < 300; i++)
        {
            channel.Deliver(WebRtcFraming.Encode(null, new byte[] { (byte)i }));
        }

        Assert.ThrowsAsync<IOException>(async () =>
        {
            while (await stream.ReadAsync().AsTask().WaitAsync(TestTimeout) is not null)
            {
            }
        });
    }

    [Test]
    public async Task Reset_AbortsAWriteParkedOnBackpressure()
    {
        // The abort path must not queue behind the write it exists to cancel.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);
        channel.SetBufferedAmount(WebRtcStream.MaxBufferedAmount);

        Task write = stream.WriteAsync(new byte[] { 1 }).AsTask();
        await Task.Delay(20);
        Assert.That(write.IsCompleted, Is.False, "write should be parked on backpressure");

        await stream.ResetAsync().AsTask().WaitAsync(TestTimeout);

        Assert.ThrowsAsync<IOException>(async () => await write);
    }

    [Test]
    public async Task ChannelClose_FailsAWriteParkedOnBackpressure()
    {
        // A closed channel never drains, so without a failure the writer re-parks forever.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);
        channel.SetBufferedAmount(WebRtcStream.MaxBufferedAmount);

        Task write = stream.WriteAsync(new byte[] { 1 }).AsTask();
        await Task.Delay(20);
        Assert.That(write.IsCompleted, Is.False);

        channel.Close();

        Assert.ThrowsAsync<IOException>(async () => await write.WaitAsync(TestTimeout));
    }

    [Test]
    public async Task AbruptChannelClose_IsAFailureNotEndOfStream()
    {
        // Truncation must be distinguishable from a clean half-close, or it is silent data loss.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);

        channel.Deliver(WebRtcFraming.Encode(null, new byte[] { 7 }));
        Assert.That(await stream.ReadAsync(), Is.EqualTo(new byte[] { 7 }));

        channel.Close();

        Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync());
    }

    [Test]
    public async Task StopSending_ReleasesAPendingCloseWrites()
    {
        // A peer that stopped reading will never ack, so the close must not burn the full timeout.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel, TimeSpan.FromSeconds(30));

        Task close = stream.CloseWritesAsync();
        await WaitUntil(() => Flags(channel).Contains(Message.Types.Flag.Fin));

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.StopSending, null));

        await close.WaitAsync(TestTimeout);
    }

    [Test]
    public async Task FinAck_IsSentEvenWhenTheChannelIsCongested()
    {
        // Flag frames must bypass the congestion gate, or the peer burns its whole FIN_ACK timeout.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel);
        channel.SetBufferedAmount(WebRtcStream.MaxBufferedAmount * 10);

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));

        await WaitUntil(() => Flags(channel).Contains(Message.Types.Flag.FinAck));
        Assert.That(await stream.ReadAsync(), Is.Null);
    }

    [Test]
    public async Task FinAckTimeout_ClosesTheDataChannel()
    {
        // Otherwise a peer that never acks leaks the channel for the lifetime of the connection.
        FakeWebRtcDataChannel channel = new();
        WebRtcStream stream = new(channel, TimeSpan.FromMilliseconds(50));

        await stream.CloseWritesAsync().WaitAsync(TestTimeout);

        await WaitUntil(() => channel.IsClosed);
        Assert.That(channel.IsClosed, Is.True);
    }
}
