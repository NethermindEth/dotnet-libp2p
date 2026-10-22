// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// Covers only what is reachable without a live association. Everything here runs against a real
/// <see cref="RTCSctpTransport"/> that was never started, so its association sits in
/// <see cref="SctpAssociationState.Closed"/>: SendData is a logged no-op and SendBufferedAmount is
/// 0. Nothing is mocked -- these are the real types in their initial state. The DCEP exchange,
/// inbound dispatch and backpressure under load need DTLS+SCTP and belong with the interop tests.
/// </summary>
[TestFixture]
[CancelAfter(15000)]
public class WebRtcDirectSctpMuxerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static RTCSctpTransport CreateTransport() => new(5000, 5000, RTCPeerSctpAssociation.DEFAULT_DTLS_MTU);

    [Test]
    public async Task OutboundStreamIds_AsDtlsClient_StartAtTwoAndStepByTwo()
    {
        RTCSctpTransport transport = CreateTransport();
        await using WebRtcDirectSctpMuxer muxer = new(transport, isDtlsClient: true);

        ushort[] ids = [.. Enumerable.Range(0, 4).Select(_ => muxer.NextOutboundStreamId())];

        Assert.That(ids, Is.EqualTo(new ushort[] { 2, 4, 6, 8 }));
    }

    [Test]
    public async Task OutboundStreamIds_AsDtlsServer_StartAtOneAndStepByTwo()
    {
        RTCSctpTransport transport = CreateTransport();
        await using WebRtcDirectSctpMuxer muxer = new(transport, isDtlsClient: false);

        ushort[] ids = [.. Enumerable.Range(0, 4).Select(_ => muxer.NextOutboundStreamId())];

        Assert.That(ids, Is.EqualTo(new ushort[] { 1, 3, 5, 7 }));
    }

    /// <summary>
    /// Id 0 is the negotiated Noise channel; handing it out for a libp2p stream would hijack the
    /// handshake. The DTLS client's half of the space is the even ids, so it is the role that has
    /// to skip it.
    /// </summary>
    [TestCase(true, 32767, (ushort)65534)]
    [TestCase(false, 32768, (ushort)65535)]
    public async Task OutboundStreamIds_AreNeverZeroAndRunOutRatherThanWrap(bool isDtlsClient, int expectedCount, ushort expectedLast)
    {
        RTCSctpTransport transport = CreateTransport();
        await using WebRtcDirectSctpMuxer muxer = new(transport, isDtlsClient);

        int count = 0;
        ushort last = 0;
        HashSet<ushort> seen = [];

        while (true)
        {
            ushort streamId;
            try
            {
                streamId = muxer.NextOutboundStreamId();
            }
            catch (InvalidOperationException)
            {
                break;
            }

            Assert.That(streamId, Is.Not.Zero);
            Assert.That(seen.Add(streamId), Is.True, $"stream id {streamId} was handed out twice");
            last = streamId;
            count++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(expectedCount));
            Assert.That(last, Is.EqualTo(expectedLast));
        });

        // Still refused after the first refusal, rather than wrapping back into the peer's parity.
        Assert.Throws<InvalidOperationException>(() => _ = muxer.NextOutboundStreamId());
    }

    [Test]
    public async Task ConcurrentChannels_AreCappedAndTheCapCountsOnlyLiveChannels()
    {
        RTCSctpTransport transport = CreateTransport();
        await using WebRtcDirectSctpMuxer muxer = new(transport, isDtlsClient: true);

        List<SctpWebRtcDataChannel> opened = [];
        for (int i = 0; i < WebRtcDirectSctpMuxer.MaxConcurrentChannels; i++)
        {
            Assert.That(muxer.TryRegisterChannel((ushort)(i + 1), out SctpWebRtcDataChannel channel), Is.True);
            opened.Add(channel);
        }

        Assert.That(muxer.TryRegisterChannel(60000, out _), Is.False);

        // An already registered stream is not a new channel, so it is still served at the cap.
        Assert.That(muxer.TryRegisterChannel(1, out SctpWebRtcDataChannel existing), Is.True);
        Assert.That(existing, Is.SameAs(opened[0]));

        opened[0].Close();

        Assert.That(muxer.TryRegisterChannel(60000, out _), Is.True);
    }

    /// <summary>
    /// Pins the exact wire bytes rather than re-deriving them: 0x03 OPEN, 0x00
    /// DATA_CHANNEL_RELIABLE, priority 0, reliability 0, and zero-length label and protocol.
    /// </summary>
    [Test]
    public void DcepOpenMessage_HasTheExpectedTwelveBytes()
    {
        Assert.That(
            WebRtcDirectSctpMuxer.CreateDcepOpenMessage(),
            Is.EqualTo(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Test]
    public async Task NegotiatedChannel_IsTheSameInstanceOnEveryCall()
    {
        RTCSctpTransport transport = CreateTransport();
        await using WebRtcDirectSctpMuxer muxer = new(transport, isDtlsClient: true);

        SctpWebRtcDataChannel first = muxer.GetNegotiatedChannel(WebRtcDirectSctpMuxer.NoiseStreamId);
        SctpWebRtcDataChannel second = muxer.GetNegotiatedChannel(WebRtcDirectSctpMuxer.NoiseStreamId);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That(first.StreamId, Is.Zero);
            // The transport never connected, so the handshake channel must not look open yet.
            Assert.That(first.Opened.IsCompleted, Is.False);
        });
    }

    [Test]
    public async Task DisposeAsync_ClosesEveryChannelAndIsIdempotent()
    {
        RTCSctpTransport transport = CreateTransport();
        WebRtcDirectSctpMuxer muxer = new(transport, isDtlsClient: true);
        SctpWebRtcDataChannel channel = muxer.GetNegotiatedChannel(WebRtcDirectSctpMuxer.NoiseStreamId);

        await muxer.DisposeAsync();
        await muxer.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(channel.IsClosed, Is.True);
            Assert.That(muxer.Completed.IsCompletedSuccessfully, Is.True);
        });
    }

    [Test]
    public void Channel_ClosesOnce_HoweverManyTimesCloseIsCalled()
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        int closedCount = 0;
        channel.Closed += () => Interlocked.Increment(ref closedCount);

        channel.Close();
        channel.Close();
        channel.Close();

        Assert.Multiple(() =>
        {
            Assert.That(closedCount, Is.EqualTo(1));
            Assert.That(channel.IsClosed, Is.True);
        });
    }

    [Test]
    public async Task Channel_DeliversAnInboundFrameToSubscribers()
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.MessageReceived += message => received.TrySetResult(message);

        channel.Receive(new SctpDataFrame(false, 2, 0, (uint)DataChannelPayloadProtocols.WebRTC_Binary, [1, 2, 3]));

        Assert.That(await received.Task.WaitAsync(TestTimeout), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    [Test]
    public async Task Channel_DoesNotDeliverToAnUnsubscribedHandler()
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        int handled = 0;
        TaskCompletionSource first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource second = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(byte[] message)
        {
            Interlocked.Increment(ref handled);
            first.TrySetResult();
        }

        channel.MessageReceived += Handler;
        channel.Receive(new SctpDataFrame(false, 2, 0, (uint)DataChannelPayloadProtocols.WebRTC_Binary, [1]));

        // Dispatch is asynchronous, so wait for the first message to land before unsubscribing;
        // otherwise this would pass for the wrong reason whenever the pump had not run yet.
        await first.Task.WaitAsync(TestTimeout);

        channel.MessageReceived -= Handler;
        channel.MessageReceived += _ => second.TrySetResult();
        channel.Receive(new SctpDataFrame(false, 2, 1, (uint)DataChannelPayloadProtocols.WebRTC_Binary, [2]));
        await second.Task.WaitAsync(TestTimeout);

        Assert.That(Volatile.Read(ref handled), Is.EqualTo(1));
    }

    /// <summary>
    /// An empty WebRTC message arrives as a single padding byte under an *_Empty PPID; passing it
    /// on would corrupt the frame reassembly downstream.
    /// </summary>
    [Test]
    public async Task Channel_DropsTheEmptyMessagePaddingByte()
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.MessageReceived += message => received.TrySetResult(message);

        channel.Receive(new SctpDataFrame(false, 2, 0, (uint)DataChannelPayloadProtocols.WebRTC_Binary_Empty, [0]));
        channel.Receive(new SctpDataFrame(false, 2, 1, (uint)DataChannelPayloadProtocols.WebRTC_Binary, [7]));

        Assert.That(await received.Task.WaitAsync(TestTimeout), Is.EqualTo(new byte[] { 7 }));
    }

    /// <summary>
    /// The invariant a parked writer depends on: BufferedAmountLow only ever fires below
    /// <see cref="WebRtcStream.MaxBufferedAmount"/>, so WaitForCapacityAsync's re-check succeeds
    /// instead of parking again forever.
    /// </summary>
    [Test]
    public void Channel_RaisesBufferedAmountLowWhileTheSendQueueIsBelowTheStreamThreshold()
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        int raised = 0;
        channel.BufferedAmountLow += () => raised++;

        channel.PollBufferedAmount(0);

        Assert.That(raised, Is.EqualTo(1));
    }

    /// <summary>
    /// The boundary itself. On an unstarted association SendBufferedAmount is constant zero, so
    /// before the amount became a parameter this guard had no reachable test at all and four
    /// separate mutations of it survived.
    /// </summary>
    [TestCase(0, true)]
    [TestCase(WebRtcStream.MaxBufferedAmount - 1, true)]
    [TestCase(WebRtcStream.MaxBufferedAmount, false)]
    [TestCase(WebRtcStream.MaxBufferedAmount + 1, false)]
    [TestCase(WebRtcStream.MaxBufferedAmount * 1000L, false)]
    public void Channel_RaisesBufferedAmountLowOnlyStrictlyBelowTheStreamThreshold(long buffered, bool expectRaised)
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        int raised = 0;
        channel.BufferedAmountLow += () => raised++;

        channel.PollBufferedAmount(buffered);

        Assert.That(raised, Is.EqualTo(expectRaised ? 1 : 0));
    }

    [Test]
    public void Channel_DoesNotRaiseBufferedAmountLowOnAnUnsubscribedHandler()
    {
        SctpWebRtcDataChannel channel = CreateChannel();

        int raised = 0;
        void Handler() => raised++;

        channel.BufferedAmountLow += Handler;
        channel.BufferedAmountLow -= Handler;
        channel.PollBufferedAmount(0);

        Assert.That(raised, Is.Zero);
    }

    /// <summary>A channel over a real association that was never started, so nothing is in flight.</summary>
    private static SctpWebRtcDataChannel CreateChannel() => new(CreateTransport().RTCSctpAssociation, 2);
}
