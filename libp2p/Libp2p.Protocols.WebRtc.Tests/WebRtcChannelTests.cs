// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using System.Buffers;
using System.Collections.Concurrent;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
// A stalled state machine must turn the run red rather than wedge it: several of these tests
// previously hung the runner instead of failing when the behavior they name regressed.
[CancelAfter(15000)]
public class WebRtcChannelTests
{
    /// <summary>Only guards against a hang: every test here reaches its result without waiting.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RoundTrips_SmallPayloadInBothDirections()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel b = new(channelB);

        await a.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 }));
        Assert.That(await ReadPayload(b), Is.EqualTo(new byte[] { 1, 2, 3 }));

        await b.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 4, 5 }));
        Assert.That(await ReadPayload(a), Is.EqualTo(new byte[] { 4, 5 }));
    }

    [Test]
    public async Task LargeWrite_IsChunkedIntoFramesWithinTheLimit()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel b = new(channelB);

        byte[] payload = new byte[WebRtcFraming.MaxPayloadSize(false) * 3 + 123];
        new Random(1234).NextBytes(payload);

        await a.WriteAsync(new ReadOnlySequence<byte>(payload));

        List<byte> received = [];
        while (received.Count < payload.Length)
        {
            received.AddRange(await ReadPayload(b));
        }

        Assert.That(received, Is.EqualTo(payload));
        Assert.That(channelA.SentFrames, Has.Count.EqualTo(4));
        Assert.That(channelA.SentFrames.Select(frame => frame.Length), Has.All.LessThanOrEqualTo(WebRtcFraming.MaxFrameSize));
    }

    [Test]
    public async Task TwoFramesInOneMessage_AreBothDecoded()
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        byte[] first = WebRtcFraming.Encode(null, new byte[] { 1 });
        byte[] second = WebRtcFraming.Encode(null, new byte[] { 2, 3 });
        channel.Deliver([.. first, .. second]);

        Assert.That(await ReadPayload(stream), Is.EqualTo(new byte[] { 1 }));
        Assert.That(await ReadPayload(stream), Is.EqualTo(new byte[] { 2, 3 }));
    }

    [Test]
    public async Task FrameSplitAcrossTwoMessages_IsDecoded()
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        byte[] frame = WebRtcFraming.Encode(null, new byte[] { 1, 2, 3, 4, 5 });
        channel.Deliver(frame[..3]);
        channel.Deliver(frame[3..]);

        Assert.That(await ReadPayload(stream), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public async Task WriteEof_CompletesOnFinAck_AndLeavesTheReadHalfOpen()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel b = new(channelB);

        await a.WriteEofAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
        Assert.That((await b.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
        Assert.That(await a.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })), Is.EqualTo(IOResult.Ended));

        await b.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 9 }));
        Assert.That(await ReadPayload(a), Is.EqualTo(new byte[] { 9 }));
    }

    [Test]
    public async Task WriteEof_IsIdempotent()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel peer = new(channelB);

        await a.WriteEofAsync().AsTask().WaitAsync(TestTimeout);
        Task second = a.WriteEofAsync().AsTask();

        Assert.That(second.IsCompleted, Is.True);
        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));
    }

    [Test]
    public async Task StopSending_StopsPeerWrites_AndIsNotAnsweredWithResetStream()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel b = new(channelB);

        await b.CloseReadAsync();

        // A peer-driven stop is distinct from the local write half ending after WriteEofAsync.
        Assert.That(await a.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })), Is.EqualTo(IOResult.Aborted));
        Assert.That(channelA.SentFrames, Is.Empty);
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.StopSending }));
        Assert.That((await b.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task StopSending_LeavesTheSenderActingOnInboundFlags()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel peer = new(channelA);
        await using WebRtcChannel b = new(channelB);

        await b.CloseReadAsync();

        // Only completes if B still processes the FIN_ACK that answers its FIN.
        await b.WriteEofAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
    }

    [Test]
    public async Task Reset_FailsBothHalvesOnBothSides()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel b = new(channelB);

        await a.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }));
        await a.AbortAsync();

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.ResetStream }));
        Assert.That((await a.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await a.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 })), Is.EqualTo(IOResult.Aborted));
        Assert.That((await b.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await b.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 })), Is.EqualTo(IOResult.Aborted));
        Assert.That(a.GetAwaiter().IsCompleted, Is.True);
        Assert.That(b.GetAwaiter().IsCompleted, Is.True);
    }

    [Test]
    public async Task MalformedFrame_AbortsTheChannel()
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        // Length prefix of 32767, past the 16kiB a framed message may occupy.
        Assert.DoesNotThrow(() => channel.Deliver([0xFF, 0xFF, 0x01]));

        Assert.That((await stream.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })), Is.EqualTo(IOResult.Aborted));
        Assert.That(channel.IsClosed, Is.True);
    }

    /// <summary>
    /// Two application writes parked on a full send buffer must both be released. There is one
    /// waiter slot, so a second registration used to overwrite the first and leave it with nothing left to wake it --
    /// the drain released only the later write, and neither a further drain nor stream failure could
    /// reach the earlier one. This transport hands its channel straight to application protocols,
    /// which do not serialize their writers the way the core <c>Channel</c> does.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task Backpressure_ReleasesEveryParkedWriter_NotJustTheLast(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel stream = new(channel);

        Task first = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token).AsTask();
        Task second = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 }), token).AsTask();

        await Task.Delay(100, token);
        Assert.That(first.IsCompleted, Is.False, "the first write must park on congestion");
        Assert.That(second.IsCompleted, Is.False, "and so must the second");

        channel.Drain();

        await Task.WhenAll(first, second).WaitAsync(TestTimeout, token);
        Assert.That(channel.SentFrames, Has.Count.EqualTo(2), "both payloads must reach the wire");
    }

    /// <summary>
    /// Teardown has to reach writers queued behind the application send lock, not just the one
    /// holding it. <c>SemaphoreSlim.Dispose</c> does not complete an existing <c>WaitAsync</c>, so a
    /// second writer queued while the first was parked on congestion was left pending for ever and
    /// only an external token could free it.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public async Task DisposingChannel_ReleasesWritersQueuedOnTheApplicationSendLock(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel stream = new(channel);

        Task<IOResult> holding = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token).AsTask();
        await Task.Delay(100, token);
        Task<IOResult> queued = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 }), token).AsTask();
        await Task.Delay(100, token);

        Assert.That(holding.IsCompleted, Is.False, "the first write must be parked on congestion");
        Assert.That(queued.IsCompleted, Is.False, "the second must be queued behind it");

        await stream.DisposeAsync();

        await Task.WhenAll(holding, queued).WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.Multiple(() =>
        {
            Assert.That(holding.Result, Is.EqualTo(IOResult.Aborted));
            Assert.That(queued.Result, Is.EqualTo(IOResult.Aborted));
        });
    }

    /// <summary>
    /// A terminal send event landing between the congestion sample and the waiter being registered
    /// must not be lost. Its ReleaseWriter finds no waiter, so a writer that then registers one and
    /// waits is waiting for a wake-up that has already happened. STOP_SENDING is the case that
    /// cannot be covered by cancellation alone: it ends this side's writing without ending the
    /// stream, so the state has to be re-read after registering.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    public async Task Backpressure_TerminalEventRacingTheWaiterRegistration_StillEndsTheWrite(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel stream = new(channel);

        int reads = 0;
        channel.OnBufferedAmountRead = () =>
        {
            // On the first sample only: this is after the writer's terminal-state check and before
            // it has published anything for ReleaseWriter to find.
            if (Interlocked.Increment(ref reads) == 1)
            {
                channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.StopSending, null));
            }
        };

        Task<IOResult> write = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token).AsTask();

        Assert.That(await write.WaitAsync(TimeSpan.FromSeconds(5), token), Is.EqualTo(IOResult.Aborted),
            "a write whose peer stopped reading must end, not hang");
    }

    [Test]
    public async Task Backpressure_HoldsTheWriteUntilTheChannelDrains()
    {
        FakeWebRtcDataChannel channel = new();
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel stream = new(channel);

        Task<IOResult> write = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[WebRtcFraming.MaxPayloadSize(false) * 2])).AsTask();

        Assert.That(write.IsCompleted, Is.False);
        Assert.That(channel.SentFrames, Is.Empty);

        channel.Drain();

        Assert.That(await write.WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));
        Assert.That(channel.SentFrames, Has.Count.EqualTo(2));
    }

    private static async Task<byte[]> ReadPayload(WebRtcChannel channel)
    {
        ReadResult read = await channel.ReadAsync(0, ReadBlockingMode.WaitAny).AsTask().WaitAsync(TestTimeout);
        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        return read.Data.ToArray();
    }

    private static Message.Types.Flag[] Flags(FakeWebRtcDataChannel channel) =>
        [.. channel.SentFrames.Select(Decode).Where(message => message.HasFlag).Select(message => message.Flag)];

    private static Message Decode(byte[] frame)
    {
        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out _), Is.True);
        return message!;
    }

    [Test]
    public async Task WriteEof_StaysPendingUntilFinAckArrives()
    {
        // "A node SHOULD only consider its write-half closed once it has received a FIN_ACK."
        // Without this the FIN_ACK wait can be deleted outright and every other test still passes.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        Task close = stream.WriteEofAsync().AsTask();
        await WaitUntilAsync(() => Flags(channel).Contains(Message.Types.Flag.Fin));
        Assert.That(close.IsCompleted, Is.False, "write half closed before any FIN_ACK arrived");

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        await close.WaitAsync(TestTimeout);
    }

    [Test]
    public async Task CleanFinExchange_ClosesTheDataChannel()
    {
        // "When a FIN_ACK and a FIN have been received, the node may close the datachannel."
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel a = new(channelA);
        await using WebRtcChannel b = new(channelB);

        await a.WriteEofAsync().AsTask().WaitAsync(TestTimeout);
        await b.WriteEofAsync().AsTask().WaitAsync(TestTimeout);

        await WaitUntilAsync(() => channelA.IsClosed);
        Assert.That(channelA.IsClosed, Is.True);
    }

    [Test]
    public async Task PayloadArrivingAfterFin_IsNotDelivered()
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));
        channel.Deliver(WebRtcFraming.Encode(null, new byte[] { 9 }));

        Assert.That((await stream.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended), "payload after FIN must not reach the reader");

        // The read half being complete already drops the payload, so that assertion alone cannot
        // see the guard disappear. What the guard prevents is treating the stray frame as an
        // overflow and failing a stream whose write half is still perfectly usable.
        Assert.That(await stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })), Is.EqualTo(IOResult.Ok),
            "a stray post-FIN payload must not fail the still-open write half");
    }

    [Test]
    public async Task InboundOverflow_AbortsTheChannel()
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        for (int i = 0; i < 300; i++)
        {
            channel.Deliver(WebRtcFraming.Encode(null, new byte[] { (byte)i }));
        }

        Assert.That((await stream.ReadAsync(1).AsTask().WaitAsync(TestTimeout)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task Reset_AbortsAWriteParkedOnBackpressure()
    {
        // The abort path must not queue behind the write it exists to cancel.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);

        Task<IOResult> write = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await Task.Delay(20);
        Assert.That(write.IsCompleted, Is.False, "write should be parked on backpressure");

        await stream.AbortAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(await write.WaitAsync(TestTimeout), Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task ChannelClose_FailsAWriteParkedOnBackpressure()
    {
        // A closed channel never drains, so without a failure the writer re-parks forever.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);

        Task<IOResult> write = stream.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })).AsTask();
        await Task.Delay(20);
        Assert.That(write.IsCompleted, Is.False);

        channel.Close();

        Assert.That(await write.WaitAsync(TestTimeout), Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task AbruptChannelClose_IsAFailureNotEndOfStream()
    {
        // Truncation must be distinguishable from a clean half-close, or it is silent data loss.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        channel.Deliver(WebRtcFraming.Encode(null, new byte[] { 7 }));
        Assert.That(await ReadPayload(stream), Is.EqualTo(new byte[] { 7 }));

        channel.Close();

        Assert.That((await stream.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task StopSending_ReleasesWriteEof(bool beforeFin)
    {
        // A peer that stopped reading has no delivery left to confirm, so the close must not burn
        // the full timeout, whichever of STOP_SENDING and the local FIN comes first.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel, TimeSpan.FromSeconds(30));

        if (beforeFin) channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.StopSending, null));
        Task<IOResult> close = stream.WriteEofAsync().AsTask();
        await WaitUntilAsync(() => Flags(channel).Contains(Message.Types.Flag.Fin));
        if (!beforeFin) channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.StopSending, null));

        Assert.That(await close.WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));
        Assert.That(channel.IsClosed, Is.False, "an unacknowledged FIN must not close the data channel early");
    }

    [Test]
    public async Task CloseAfterThePeerClosedAndWentAway_DoesNotWaitForFinAck(CancellationToken token)
    {
        // The usual request/response ending: the peer answers, closes the whole stream and its
        // owner disposes it, so nothing is left there to acknowledge the FIN sent afterwards.
        (FakeWebRtcDataChannel localDataChannel, FakeWebRtcDataChannel remoteDataChannel) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel local = new(localDataChannel, TimeSpan.FromSeconds(30));
        WebRtcChannel remote = new(remoteDataChannel);

        Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1, 2 }), token), Is.EqualTo(IOResult.Ok));
        await remote.CloseAsync().AsTask().WaitAsync(TestTimeout, token);
        await remote.DisposeAsync();

        ReadResult answer = await local.ReadAsync(2, token: token);
        Assert.That(answer.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        await local.CloseAsync().AsTask().WaitAsync(TestTimeout, token);

        Assert.That(Flags(localDataChannel), Is.EqualTo(new[] { Message.Types.Flag.FinAck, Message.Types.Flag.Fin }));
        Assert.That(local.GetAwaiter().IsCompleted, Is.True);
    }

    [Test]
    public async Task FinAck_IsSentEvenWhenTheChannelIsCongested()
    {
        // Flag frames must bypass the congestion gate, or the peer burns its whole FIN_ACK timeout.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);
        channel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount * 10);

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));

        await WaitUntilAsync(() => Flags(channel).Contains(Message.Types.Flag.FinAck));
        Assert.That((await stream.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task FinAckTimeout_ClosesTheDataChannel()
    {
        // Otherwise a peer that never acks leaks the channel for the lifetime of the connection.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel, TimeSpan.FromMilliseconds(50));

        await stream.WriteEofAsync().AsTask().WaitAsync(TestTimeout);

        await WaitUntilAsync(() => channel.IsClosed);
        Assert.That(channel.IsClosed, Is.True);
    }

    [Test]
    public async Task RepeatedFin_IsAnsweredExactlyOnce()
    {
        // Otherwise a peer drives one detached FIN_ACK task per FIN frame it sends.
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel stream = new(channel);

        for (int i = 0; i < 50; i++)
        {
            channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));
        }

        await WaitUntilAsync(() => Flags(channel).Contains(Message.Types.Flag.FinAck));
        await Task.Delay(50);

        Assert.That(Flags(channel).Count(f => f == Message.Types.Flag.FinAck), Is.EqualTo(1));
        Assert.That((await stream.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
    }

    [Test]
    public async Task WaitAll_AssemblesOneReadFromSeveralFrames()
    {
        // Frame boundaries are a framing artifact the reader never chose, so an exact-length read
        // has to span them.
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel sender = new(channelA);
        await using WebRtcChannel channel = new(channelB);

        byte[] payload = new byte[100];
        new Random(7).NextBytes(payload);
        foreach (Range part in new[] { 0..30, 30..61, 61..100 })
        {
            await sender.WriteAsync(new ReadOnlySequence<byte>(payload[part]));
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
        await using WebRtcChannel channel = new(channelA);
        await using WebRtcChannel remote = new(channelB);

        await channel.CloseAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin, Message.Types.Flag.StopSending }));
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
        Assert.That(await remote.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 })), Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task WriteEofThenClose_StillSendsOneFin()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel channel = new(channelA);
        await using WebRtcChannel remote = new(channelB);

        Assert.That(await channel.WriteEofAsync().AsTask().WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));

        // Asserted before CloseAsync as well, or a WriteEofAsync that sent nothing at all would
        // still satisfy the one-FIN total below.
        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin }));

        await channel.CloseAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.Fin, Message.Types.Flag.StopSending }));
    }

    [Test]
    public async Task CloseAfterPeerFin_DoesNotSendStopSendingOnClosedTransport()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel channel = new(channelA);
        await using WebRtcChannel remote = new(channelB);

        await remote.WriteEofAsync().AsTask().WaitAsync(TestTimeout);
        await channel.CloseAsync().AsTask().WaitAsync(TestTimeout);

        Assert.That(channelA.IsClosed, Is.True);
        Assert.That(Flags(channelA), Is.EqualTo(new[] { Message.Types.Flag.FinAck, Message.Types.Flag.Fin }));
        Assert.That(Flags(channelB), Is.EqualTo(new[] { Message.Types.Flag.Fin, Message.Types.Flag.FinAck }));
    }

    [Test]
    public async Task CloseReadAsync_DoesNotThrowAfterAPeerReset()
    {
        // The IChannel close calls return no result and Yamux discards the task, so a throw here
        // would surface only as an unobserved task exception.
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel channel = new(channelB);

        await channelA.SendAsync(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null));

        Assert.That(async () => await channel.CloseReadAsync().AsTask().WaitAsync(TestTimeout), Throws.Nothing);

        // Swallowed, not lost. The awaiter is what a caller that is neither reading nor writing
        // has to learn from, so a close that failed has to finish the channel rather than leave
        // it pending forever.
        Assert.That(channel.GetAwaiter().IsCompleted, Is.True);

        // And the failure still reaches a reader as an abort.
        using CancellationTokenSource cts = new(TestTimeout);
        ReadResult read = await channel.ReadAsync(1, ReadBlockingMode.WaitAll, cts.Token);
        Assert.That(read.Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task WriteEof_CompletesTheChannelOnlyOnceTheReadHalfEndsToo()
    {
        // The awaiter drives IChannel.CancellationToken, so completing it on a half-close would
        // cancel callers that are still legitimately reading.
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel channel = new(channelA);
        await using WebRtcChannel remote = new(channelB);

        Assert.That(await channel.WriteEofAsync().AsTask().WaitAsync(TestTimeout), Is.EqualTo(IOResult.Ok));
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False);

        await remote.WriteEofAsync().AsTask().WaitAsync(TestTimeout);

        using CancellationTokenSource cts = new(TestTimeout);
        ReadResult read = await channel.ReadAsync(1, ReadBlockingMode.WaitAll, cts.Token);

        Assert.That(read.Result, Is.EqualTo(IOResult.Ended));
        Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
    }

    [Test]
    public async Task PeerFin_EndsTheReadCleanly()
    {
        (FakeWebRtcDataChannel channelA, FakeWebRtcDataChannel channelB) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel channel = new(channelB);

        await channelA.SendAsync(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));

        using CancellationTokenSource cts = new(TestTimeout);
        ReadResult read = await channel.ReadAsync(1, ReadBlockingMode.WaitAll, cts.Token);

        Assert.That(read.Result, Is.EqualTo(IOResult.Ended));

        // Our own write half is still open, so the channel as a whole is not finished yet.
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False);
    }

    /// <summary>
    /// A peer RESET_STREAM has to complete the awaiter and cancel the channel token. Callers wait on
    /// exactly that: <c>PubsubProtocol.DialAsync</c> awaits the channel before releasing its
    /// outbound registration, and <c>RequestResponseProtocol.ListenAsync</c> gives the channel's
    /// token to a handler. Completion must not depend on another application read or write.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task RemoteReset_CompletesTheAwaiter_WithNoIoPending(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel subject = new(channel);

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null));

        await WaitUntilAsync(() => subject.GetAwaiter().IsCompleted, token);
        Assert.That((await subject.ReadAsync(1, ReadBlockingMode.WaitAll, token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Reset_DoesNotPublishEofBeforeDataChannelCloseCompletes(bool local, CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        (WebRtcChannel channel, InlineSynchronizationContext deliveryContext) = CreateChannelWithInlineDelivery(dataChannel);

        await using (channel)
        {
            ReadResult duringClose = default;
            Task<ReadResult>? pendingRead = null;
            dataChannel.OnClose = () =>
            {
                // Finish the pump's queue-completion continuation while transport teardown is
                // still in progress, before Fail can abort the Core channel. No sleep is needed.
                deliveryContext.ContinuationCompleted.Task.WaitAsync(TestTimeout, token).GetAwaiter().GetResult();
                duringClose = channel.ReadAsync(1, ReadBlockingMode.DoNotWait, token).GetAwaiter().GetResult();
                pendingRead = channel.ReadAsync(1, token: token).AsTask();
            };

            if (local)
                await channel.AbortAsync();
            else
                dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null));

            Assert.That(duringClose.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(duringClose.Data.IsEmpty, Is.True);
            Assert.That((await pendingRead!.WaitAsync(TestTimeout, token)).Result, Is.EqualTo(IOResult.Aborted));
            Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
        }
    }

    [Test]
    public async Task CloseRead_DoesNotPublishEofBeforeStopSendingCompletes(CancellationToken token)
    {
        (FakeWebRtcDataChannel local, FakeWebRtcDataChannel remote) = FakeWebRtcDataChannel.CreatePair();
        (WebRtcChannel channel, InlineSynchronizationContext deliveryContext) = CreateChannelWithInlineDelivery(local);
        await using (channel)
        {
            ReadResult duringClose = default;
            remote.MessageReceived += _ =>
            {
                deliveryContext.ContinuationCompleted.Task.WaitAsync(TestTimeout, token).GetAwaiter().GetResult();
                duringClose = channel.ReadAsync(1, ReadBlockingMode.DoNotWait, token).GetAwaiter().GetResult();
            };

            await channel.CloseReadAsync();

            Assert.That(Flags(local), Is.EqualTo(new[] { Message.Types.Flag.StopSending }));
            Assert.That(duringClose.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(duringClose.Data.IsEmpty, Is.True);
            Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
            Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token), Is.EqualTo(IOResult.Ok));
        }
    }

    /// <summary>The datachannel going away underneath is the same class of event as a reset.</summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task ChannelClosing_CompletesTheAwaiter_WithNoIoPending(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel subject = new(channel);

        channel.Close();

        await WaitUntilAsync(() => subject.GetAwaiter().IsCompleted, token);
        Assert.That((await subject.ReadAsync(1, ReadBlockingMode.WaitAll, token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    /// <summary>
    /// A received FIN ends the read half and nothing else. If the whole datachannel then goes away
    /// while our write half is still open, that is the transport being lost, not an orderly
    /// ending -- and the channel's awaiter has to complete for it.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task ChannelClosingAfterPeerFin_StillCompletesWholeChannel(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        await using WebRtcChannel subject = new(channel);

        channel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));
        Assert.That((await subject.ReadAsync(1, ReadBlockingMode.WaitAll, token)).Result, Is.EqualTo(IOResult.Ended),
            "the read half must end on the peer's FIN");
        Assert.That(subject.GetAwaiter().IsCompleted, Is.False, "but the write half is still open");

        channel.Close();

        await WaitUntilAsync(() => subject.GetAwaiter().IsCompleted, token);
    }

    /// <summary>
    /// A stream can be accepted and queued before the session loop wraps it, and the channel can
    /// close in between. The Closed event has already been and gone by the time anything subscribes,
    /// so the terminal state has to be readable rather than only observable as an event.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task DataChannelClosedBeforeConstruction_IsStillTerminal(CancellationToken token)
    {
        FakeWebRtcDataChannel channel = new();
        channel.Close();

        await using WebRtcChannel subject = new(channel);

        await WaitUntilAsync(() => subject.GetAwaiter().IsCompleted, token);
        Assert.That((await subject.ReadAsync(1, ReadBlockingMode.WaitAll, token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task PartialReads_KeepTheTailAndPreviouslyReturnedBytes(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3, 4 }));

        ReadResult head = await channel.ReadAsync(2, token: token);
        ReadResult tail = await channel.ReadAsync(2, token: token);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 5, 6 }));
        ReadResult next = await channel.ReadAsync(2, token: token);

        Assert.Multiple(() =>
        {
            Assert.That(head.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(tail.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(next.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(head.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(tail.Data.ToArray(), Is.EqualTo(new byte[] { 3, 4 }));
            Assert.That(next.Data.ToArray(), Is.EqualTo(new byte[] { 5, 6 }));
        });
    }

    [Test]
    public async Task ConcurrentReads_ConsumeDisjointBytesInOrder(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        Task<ReadResult> first = channel.ReadAsync(2, token: token).AsTask();
        Task<ReadResult> second = channel.ReadAsync(2, token: token).AsTask();

        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3, 4 }));
        ReadResult[] reads = await Task.WhenAll(first, second).WaitAsync(TestTimeout, token);

        Assert.That(reads.Select(read => read.Result), Is.All.EqualTo(IOResult.Ok));
        Assert.That(reads[0].Data.ToArray(), Is.EqualTo(new byte[] { 1, 2 }));
        Assert.That(reads[1].Data.ToArray(), Is.EqualTo(new byte[] { 3, 4 }));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExactZeroRead_AfterPeerTermination_PreservesResult(bool reset, CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 0 }));
        IReader reader = channel;
        Assert.That(await reader.ReadVarintAsync(token), Is.Zero);

        dataChannel.Deliver(WebRtcFraming.Encode(reset ? Message.Types.Flag.ResetStream : Message.Types.Flag.Fin, null));
        Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(reset ? IOResult.Aborted : IOResult.Ended));
        ReadResult body = await channel.ReadAsync(0, token: token);
        Assert.That(body.Result, Is.EqualTo(reset ? IOResult.Aborted : IOResult.Ok));
        Assert.That(body.Data.IsEmpty, Is.True);
    }

    [Test]
    public async Task ReadWithoutWaiting_ReturnsAvailableBytesAndThenEmpty(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        ReadResult empty = await channel.ReadAsync(0, ReadBlockingMode.DoNotWait, token);
        Assert.That(empty.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(empty.Data.IsEmpty, Is.True);

        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3 }));
        ReadResult available = empty;
        for (int attempt = 0; attempt < 500 && available.Data.IsEmpty; attempt++)
        {
            available = await channel.ReadAsync(0, ReadBlockingMode.DoNotWait, token);
            if (available.Data.IsEmpty) await Task.Delay(10, token);
        }

        Assert.That(available.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(available.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That((await channel.ReadAsync(0, ReadBlockingMode.DoNotWait, token)).Data.IsEmpty, Is.True);
    }

    [Test]
    public async Task CancelledRead_DoesNotPreventTheNextRead(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ReadResult> reading = channel.ReadAsync(1, token: cancelled.Token).AsTask();
        cancelled.Cancel();

        Assert.That((await reading.WaitAsync(TestTimeout, token)).Result, Is.EqualTo(IOResult.Cancelled));
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 7 }));
        ReadResult next = await channel.ReadAsync(1, token: token);
        Assert.That(next.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(next.Data.ToArray(), Is.EqualTo(new byte[] { 7 }));
    }

    [Test]
    public async Task UnreadPayload_DoesNotBlockFinOrFinAck_AndDelaysCompletion(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3 }));

        Task<IOResult> eof = channel.WriteEofAsync(token).AsTask();
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));

        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));
        await WaitUntilAsync(() => Flags(dataChannel).Contains(Message.Types.Flag.FinAck), token);
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False, "Received payload is still owed to the application.");

        ReadResult read = await channel.ReadAsync(3, token: token);
        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
        await WaitUntilAsync(() => channel.GetAwaiter().IsCompleted, token);
    }

    [Test]
    public async Task UnreadPayload_DoesNotBlockStopSending(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel channel = new(dataChannel);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3 }));
        Task<IOResult> writing = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 9 }), token).AsTask();

        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.StopSending, null));

        Assert.That(await writing.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Aborted));
        Assert.That(Flags(dataChannel), Does.Not.Contain(Message.Types.Flag.ResetStream));
        ReadResult read = await channel.ReadAsync(3, token: token);
        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task UnreadPayload_DoesNotBlockReset_AndIsDiscarded(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3 }));
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.ResetStream, null));

        await WaitUntilAsync(() => channel.GetAwaiter().IsCompleted, token);
        Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task CloseRead_DiscardsUnreadPayloadAndKeepsProcessingControl(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        dataChannel.Deliver(WebRtcFraming.Encode(null, new byte[] { 1, 2, 3 }));

        await channel.CloseReadAsync().AsTask().WaitAsync(TestTimeout, token);
        Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False, "Only the local read half is closed.");

        Task<IOResult> eof = channel.WriteEofAsync(token).AsTask();
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
        Assert.That(Flags(dataChannel), Is.EqualTo(new[] { Message.Types.Flag.StopSending, Message.Types.Flag.Fin }));
        await WaitUntilAsync(() => channel.GetAwaiter().IsCompleted, token);
    }

    [Test]
    public async Task CancelledCongestedWrite_DoesNotPreventTheNextWrite(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel channel = new(dataChannel);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IOResult> writing = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), cancelled.Token).AsTask();
        cancelled.Cancel();

        Assert.That(await writing.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Cancelled));
        dataChannel.Drain();
        Assert.That(await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2 }), token), Is.EqualTo(IOResult.Ok));
        Assert.That(dataChannel.SentFrames, Has.Count.EqualTo(1));
        Assert.That(Decode(dataChannel.SentFrames[0]).Message_.ToByteArray(), Is.EqualTo(new byte[] { 2 }));
    }

    [Test]
    public async Task CancellationAfterFirstFrame_CompletesTheCommittedWrite(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        int cancellationTriggered = 0;
        dataChannel.OnBufferedAmountRead = () =>
        {
            if (dataChannel.SentFrames.Count == 1 && Interlocked.Exchange(ref cancellationTriggered, 1) == 0)
            {
                dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
                cancelled.Cancel();
            }
        };
        byte[] payload = new byte[WebRtcFraming.MaxPayloadSize(false) + 3];
        new Random(42).NextBytes(payload);

        Task<IOResult> writing = channel.WriteAsync(new ReadOnlySequence<byte>(payload), cancelled.Token).AsTask();
        Assert.That(cancelled.IsCancellationRequested, Is.True);
        Assert.That(writing.IsCompleted, Is.False, "The already-delivered prefix commits the remaining bytes.");

        dataChannel.Drain();
        Assert.That(await writing.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
        Assert.That(dataChannel.SentFrames.SelectMany(frame => Decode(frame).Message_.ToByteArray()), Is.EqualTo(payload));
    }

    [Test]
    public async Task SegmentedWrite_PreservesTheByteSequence(CancellationToken token)
    {
        (FakeWebRtcDataChannel from, FakeWebRtcDataChannel to) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel sender = new(from);
        await using WebRtcChannel receiver = new(to);
        MemorySegment<byte> first = new(new byte[] { 1, 2 });
        MemorySegment<byte> last = first.Append(new byte[] { 3, 4, 5 });
        ReadOnlySequence<byte> bytes = new(first, 0, last, last.Memory.Length);

        Assert.That(await sender.WriteAsync(bytes, token), Is.EqualTo(IOResult.Ok));
        ReadResult read = await receiver.ReadAsync(5, token: token);
        Assert.That(read.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(read.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5 }));
    }

    [Test]
    public async Task ConcurrentWritesAndEof_PreserveWholeWriteOrdering(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        int samples = 0;
        dataChannel.OnBufferedAmountRead = () =>
        {
            // Park the first application write after its first frame has been sent.
            if (Interlocked.Increment(ref samples) == 2)
                dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        };
        byte[] firstPayload = new byte[WebRtcFraming.MaxPayloadSize(false) * 2 + 1];
        Array.Fill(firstPayload, (byte)1);
        Task<IOResult> first = channel.WriteAsync(new ReadOnlySequence<byte>(firstPayload), token).AsTask();
        Assert.That(dataChannel.SentFrames, Has.Count.EqualTo(1));
        Assert.That(first.IsCompleted, Is.False);

        Task<IOResult> second = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 2, 3 }), token).AsTask();
        Task<IOResult> eof = channel.WriteEofAsync(token).AsTask();
        Assert.That(second.IsCompleted, Is.False);
        Assert.That(eof.IsCompleted, Is.False);

        dataChannel.Drain();
        Assert.That(await Task.WhenAll(first, second).WaitAsync(TestTimeout, token),
            Is.EqualTo(new[] { IOResult.Ok, IOResult.Ok }));
        await WaitUntilAsync(() => Flags(dataChannel).Contains(Message.Types.Flag.Fin), token);
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));

        Message[] frames = [.. dataChannel.SentFrames.Select(Decode)];
        Assert.That(frames[^1].Flag, Is.EqualTo(Message.Types.Flag.Fin));
        Assert.That(frames[..^1].Select(frame => frame.HasFlag), Is.All.False);
        Assert.That(frames.SelectMany(frame => frame.Message_.ToByteArray()),
            Is.EqualTo(firstPayload.Concat(new byte[] { 2, 3 })));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CloseAsync_AbortsAnUnfinishedWrite(bool prefixSent, bool eofQueued, CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        if (prefixSent)
        {
            dataChannel.OnBufferedAmountRead = () =>
            {
                if (dataChannel.SentFrames.Count == 1)
                    dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
            };
        }
        else
        {
            dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        }

        byte[] payload = new byte[WebRtcFraming.MaxPayloadSize(false) + 1];
        Task<IOResult> writing = channel.WriteAsync(new ReadOnlySequence<byte>(payload)).AsTask();
        Assert.That(writing.IsCompleted, Is.False, "The application write must be waiting for capacity.");
        Assert.That(dataChannel.SentFrames, Has.Count.EqualTo(prefixSent ? 1 : 0));
        Task<IOResult>? eof = eofQueued ? channel.WriteEofAsync().AsTask() : null;
        Task closing = channel.CloseAsync().AsTask();

        try
        {
            // Unlike write EOF, whole-channel closure must release an incomplete write even
            // when the association never drains and an EOF call is already queued behind it.
            await closing.WaitAsync(TestTimeout, token);
            Assert.That(await writing.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Aborted));
            if (eof is not null)
                Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Aborted));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
        }
        finally
        {
            // Release all pending tasks when the pre-fix implementation hits the hang guard.
            await channel.AbortAsync();
            await Task.WhenAll(writing, closing).WaitAsync(TestTimeout, token);
            if (eof is not null) await eof.WaitAsync(TestTimeout, token);
        }
    }

    [Test]
    public async Task CloseAsync_DoesNotResetBehindAPendingEof(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        dataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
        await using WebRtcChannel channel = new(dataChannel);
        Task<IOResult> writing = channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token).AsTask();
        Assert.That(writing.IsCompleted, Is.False);

        // The EOF queues behind the write and resumes only when the test runs it, which holds it
        // where it owns the application send lock but has not sent its FIN yet.
        DeferredSynchronizationContext resumption = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(resumption);
        Task<IOResult> eof;
        try
        {
            eof = channel.WriteEofAsync(token).AsTask();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        dataChannel.Drain();
        Assert.That(await writing.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
        await resumption.Posted.Task.WaitAsync(TestTimeout, token);

        Task closing = channel.CloseAsync().AsTask();
        resumption.RunPosted();
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));

        await closing.WaitAsync(TestTimeout, token);
        Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
        Assert.That(Flags(dataChannel), Is.EqualTo(new[] { Message.Types.Flag.StopSending, Message.Types.Flag.Fin }),
            "a half-close in progress is not an unfinished write, so the close must not reset the stream");
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CancelledEofBeforeFin_AllowsDataAndLaterClosure(bool queuedBehindWrite, bool closeWholeChannel, CancellationToken token)
    {
        (FakeWebRtcDataChannel localDataChannel, FakeWebRtcDataChannel remoteDataChannel) = FakeWebRtcDataChannel.CreatePair();
        await using WebRtcChannel local = new(localDataChannel);
        await using WebRtcChannel remote = new(remoteDataChannel);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        byte[] payload = [1, 2, 3];
        Task<IOResult>? writing = null;
        if (queuedBehindWrite)
        {
            localDataChannel.SetBufferedAmount(WebRtcChannel.MaxBufferedAmount);
            writing = local.WriteAsync(new ReadOnlySequence<byte>(payload)).AsTask();
            Assert.That(writing.IsCompleted, Is.False);
        }
        else
        {
            cancelled.Cancel();
        }

        try
        {
            Task<IOResult> cancelledEof = local.WriteEofAsync(cancelled.Token).AsTask();
            if (queuedBehindWrite) cancelled.Cancel();
            Assert.That(await cancelledEof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Cancelled));
            Assert.That(Flags(localDataChannel), Is.Empty, "Cancellation occurred before any FIN could be sent.");

            localDataChannel.Drain();
            writing ??= local.WriteAsync(new ReadOnlySequence<byte>(payload), token).AsTask();
            Assert.That(await writing.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
            ReadResult received = await remote.ReadAsync(payload.Length, token: token);
            Assert.That(received.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(received.Data.ToArray(), Is.EqualTo(payload));

            if (closeWholeChannel)
            {
                await local.CloseAsync().AsTask().WaitAsync(TestTimeout, token);
            }
            else
            {
                Assert.That(await local.WriteEofAsync(token).AsTask().WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
            }

            Assert.That(Flags(localDataChannel), Is.EqualTo(closeWholeChannel
                ? new[] { Message.Types.Flag.Fin, Message.Types.Flag.StopSending }
                : new[] { Message.Types.Flag.Fin }));
            Assert.That(Flags(remoteDataChannel), Is.EqualTo(new[] { Message.Types.Flag.FinAck }));
            Assert.That((await remote.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
        }
        finally
        {
            await local.AbortAsync();
            if (writing is not null) await writing.WaitAsync(TestTimeout, token);
        }
    }

    [Test]
    public async Task CancellationAfterFin_StillCompletesOnAckAndPeerFin(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        using CancellationTokenSource cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<IOResult> eof = channel.WriteEofAsync(cancelled.Token).AsTask();
        Assert.That(Flags(dataChannel), Is.EqualTo(new[] { Message.Types.Flag.Fin }));

        cancelled.Cancel();
        Assert.That(eof.IsCompleted, Is.False, "FIN is committed; its acknowledgement is still owed.");
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));

        Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));
        Assert.That(channel.GetAwaiter().IsCompleted, Is.False, "The peer's write half is still open.");
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.Fin, null));
        Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Ended));
        await WaitUntilAsync(() => channel.GetAwaiter().IsCompleted, token);
    }

    [Test]
    public async Task ConcurrentCloseAsync_SharesOneFinWhileWaitingForAck(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);

        Task first = channel.CloseAsync().AsTask();
        Task second = channel.CloseAsync().AsTask();
        Assert.That(first.IsCompleted, Is.False);
        Assert.That(second.IsCompleted, Is.False);
        Assert.That(Flags(dataChannel), Is.EqualTo(new[] { Message.Types.Flag.Fin, Message.Types.Flag.StopSending }));

        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        await Task.WhenAll(first, second).WaitAsync(TestTimeout, token);

        Assert.That(Flags(dataChannel), Is.EqualTo(new[] { Message.Types.Flag.Fin, Message.Types.Flag.StopSending }));
        Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
    }

    [Test]
    public async Task ConcurrentClose_WaitsForPendingStopSending(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        Task<IOResult> eof = channel.WriteEofAsync(token).AsTask();
        dataChannel.Deliver(WebRtcFraming.Encode(Message.Types.Flag.FinAck, null));
        Assert.That(await eof.WaitAsync(TestTimeout, token), Is.EqualTo(IOResult.Ok));

        TaskCompletionSource sendingStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseStop = new();
        dataChannel.BeforeSend = _ =>
        {
            sendingStop.TrySetResult();
            releaseStop.Wait(token);
        };

        Task readClose = Task.Run(async () => await channel.CloseReadAsync(), token);
        try
        {
            await sendingStop.Task.WaitAsync(TestTimeout, token);
            Task close = channel.CloseAsync().AsTask();
            Assert.That(close.IsCompleted, Is.False, "Whole close must wait for the pending STOP_SENDING.");
            Assert.That(channel.GetAwaiter().IsCompleted, Is.False);
            releaseStop.Set();
            await Task.WhenAll(readClose, close).WaitAsync(TestTimeout, token);
            Assert.That(Flags(dataChannel), Is.EqualTo(new[] { Message.Types.Flag.Fin, Message.Types.Flag.StopSending }));
        }
        finally
        {
            releaseStop.Set();
            await readClose.WaitAsync(TestTimeout, token);
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CloseReadWaiters_AreReleasedWhenChannelFails(bool dispose, CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        TaskCompletionSource sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        dataChannel.BeforeSend = _ =>
        {
            sending.TrySetResult();
            release.Wait(token);
        };

        Task first = Task.Run(async () => await channel.CloseReadAsync(), token);
        Task? failure = null;
        try
        {
            await sending.Task.WaitAsync(TestTimeout, token);
            Task second = channel.CloseReadAsync().AsTask();
            Assert.That(second.IsCompleted, Is.False);
            failure = (dispose ? channel.DisposeAsync() : channel.AbortAsync()).AsTask();

            await second.WaitAsync(TestTimeout, token);
            Assert.That((await channel.ReadAsync(1, token: token)).Result, Is.EqualTo(IOResult.Aborted));
            Assert.That(first.IsCompleted, Is.False, "The native send is still held by the test gate.");
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TestTimeout, token);
            if (failure is not null) await failure.WaitAsync(TestTimeout, token);
        }
    }

    [Test]
    public async Task QueuedStopSending_IsCancelledWhenChannelFails(CancellationToken token)
    {
        FakeWebRtcDataChannel dataChannel = new();
        await using WebRtcChannel channel = new(dataChannel);
        TaskCompletionSource sending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        dataChannel.BeforeSend = _ =>
        {
            sending.TrySetResult();
            release.Wait(token);
        };

        Task<IOResult> write = Task.Run(async () => await channel.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 1 }), token), token);
        try
        {
            await sending.Task.WaitAsync(TestTimeout, token);
            Task readClose = channel.CloseReadAsync().AsTask();
            Assert.That(readClose.IsCompleted, Is.False);
            await channel.DisposeAsync();

            await readClose.WaitAsync(TestTimeout, token);
            Assert.That(write.IsCompleted, Is.False, "Cancelling STOP must not require releasing the native data send.");
        }
        finally
        {
            release.Set();
            await write.WaitAsync(TestTimeout, token);
        }
    }

    private static (WebRtcChannel, InlineSynchronizationContext) CreateChannelWithInlineDelivery(FakeWebRtcDataChannel dataChannel)
    {
        InlineSynchronizationContext deliveryContext = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(deliveryContext);
            return (new(dataChannel), deliveryContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public TaskCompletionSource ContinuationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            callback(state);
            ContinuationCompleted.TrySetResult();
        }
    }

    /// <summary>Holds posted continuations until the test runs them.</summary>
    private sealed class DeferredSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _posted.Enqueue((callback, state));
            Posted.TrySetResult();
        }

        public void RunPosted()
        {
            while (_posted.TryDequeue(out (SendOrPostCallback Callback, object? State) posted))
            {
                posted.Callback(posted.State);
            }
        }
    }

    /// <summary>Bounded poll for work completing on a detached task; only slows a failing run.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token = default)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, token);
        }

        Assert.That(condition(), Is.True, "condition never became true");
    }
}
