// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;
using System.Reflection;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// The parts of <see cref="RtcDataChannelAdapter"/> that do not need a live peer connection: the
/// synthesized BufferedAmountLow event, and that every way the channel can end collapses into one
/// <see cref="IWebRtcDataChannel.Closed"/> and one disposed timer. A detached
/// <see cref="RTCDataChannel"/> is enough -- it reports <c>bufferedAmount</c> 0 and closes
/// without a transport.
/// </summary>
[TestFixture]
public class RtcDataChannelAdapterTests
{
    private static RTCDataChannel DetachedChannel() => new(null!, new RTCDataChannelInit { negotiated = true, id = 0 });

    /// <summary>
    /// RFC 8832 section 6 lets a peer send data before the OPEN/ACK exchange finishes, and
    /// <c>RTCDataChannel.GotData</c> invokes its event with nothing queued behind it. So everything
    /// that lands between this adapter being constructed and the stream above it subscribing used to
    /// be dropped -- which against a real Go peer meant the multistream header vanished and the
    /// first reply read as <c>/ipfs/ping/1.0.0</c>.
    /// </summary>
    /// <remarks>
    /// Driven through <c>GotData</c> by reflection because it is internal to SIPSorcery: there is no
    /// public way to deliver an inbound datachannel message, and faking the seam instead would test
    /// the fake rather than the adapter.
    /// </remarks>
    [Test]
    [CancelAfter(10_000)]
    public void MessagesArrivingBeforeAConsumerAttaches_AreRetained(CancellationToken token)
    {
        RTCDataChannel channel = DetachedChannel();
        using RtcDataChannelAdapter adapter = new(channel);
        MethodInfo gotData = typeof(RTCDataChannel).GetMethod("GotData", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SIPSorcery no longer exposes GotData; rewrite this test against its replacement.");

        void Deliver(byte[] payload) => gotData.Invoke(channel,
            [(ushort)0, (ushort)0, (uint)DataChannelPayloadProtocols.WebRTC_Binary, payload]);

        Deliver([1, 2, 3]);

        List<byte[]> received = [];
        adapter.MessageReceived += received.Add;
        Deliver([4, 5, 6]);

        Assert.That(received, Is.EqualTo(new[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } }),
            "the message delivered before the consumer attached must still arrive, and in order");
    }

    /// <summary>
    /// Retained and live messages must come out in one order. Publishing the handler before
    /// replaying the backlog let a message that arrived during the replay overtake the queued ones:
    /// delivering 3 from inside the callback for 1 produced 1, 3, 2. The framing above splits
    /// payloads across messages, so a reordering here is a corrupt frame, not just a late one.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task RetainedAndLiveMessages_ArriveInOneOrder(CancellationToken token)
    {
        RTCDataChannel channel = DetachedChannel();
        using RtcDataChannelAdapter adapter = new(channel);
        Action<byte[]> deliver = Deliverer(channel);

        deliver([1]);
        deliver([2]);

        List<byte> seen = [];
        adapter.MessageReceived += message =>
        {
            seen.Add(message[0]);

            // Re-entrant on purpose: this is the live message arriving mid-replay.
            if (message[0] == 1)
            {
                deliver([3]);
            }
        };

        for (int i = 0; i < 500 && seen.Count < 3; i++)
        {
            await Task.Delay(10, token);
        }

        Assert.That(seen, Is.EqualTo(new byte[] { 1, 2, 3 }),
            "a message arriving during the replay must queue behind the retained ones");
    }

    /// <summary>
    /// The bound stays, but overflow has to be visible. SCTP has already delivered these bytes and
    /// will not retransmit them, so discarding the 257th while keeping the channel open silently
    /// truncates a payload -- or loses a FIN.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task OverflowingTheRetentionBound_FailsObservably(CancellationToken token)
    {
        RTCDataChannel channel = DetachedChannel();
        using RtcDataChannelAdapter adapter = new(channel);
        Action<byte[]> deliver = Deliverer(channel);

        for (int i = 0; i < 257; i++)
        {
            deliver([(byte)i]);
        }

        bool closed = false;
        adapter.Closed += () => closed = true;
        adapter.MessageReceived += _ => { };

        for (int i = 0; i < 500 && !closed; i++)
        {
            await Task.Delay(10, token);
        }

        Assert.That(closed, Is.True,
            "an overflowed retention buffer must reach the consumer as a terminal event, not as silence");
    }

    /// <summary>Delivers an inbound message the way the SCTP association would.</summary>
    private static Action<byte[]> Deliverer(RTCDataChannel channel)
    {
        MethodInfo gotData = typeof(RTCDataChannel).GetMethod("GotData", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SIPSorcery no longer exposes GotData; rewrite these tests against its replacement.");
        return payload => gotData.Invoke(channel,
            [(ushort)0, (ushort)0, (uint)DataChannelPayloadProtocols.WebRTC_Binary, payload]);
    }

    /// <summary>
    /// WebRtcStream parks a writer and waits for this event, so an adapter that never raises it
    /// deadlocks the first peer that fills the send buffer. SIPSorcery raises no such event of its
    /// own (there is no onbufferedamountlow member on RTCDataChannel in 10.0.14), which is why it
    /// is polled.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public async Task BufferedAmountLow_IsRaisedByPolling(CancellationToken token)
    {
        using RtcDataChannelAdapter adapter = new(DetachedChannel());
        TaskCompletionSource raised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.BufferedAmountLow += () => raised.TrySetResult();

        await raised.Task.WaitAsync(token);

        Assert.That(adapter.BufferedAmount, Is.LessThan(WebRtcStream.MaxBufferedAmount),
            "The event must never fire at or above the limit a parked writer re-checks against.");
    }

    [Test]
    public void Close_RaisesClosedExactlyOnce_AndIsIdempotent()
    {
        RtcDataChannelAdapter adapter = new(DetachedChannel());
        int closed = 0;
        adapter.Closed += () => Interlocked.Increment(ref closed);

        adapter.Close();
        adapter.Close();
        adapter.Dispose();

        Assert.That(closed, Is.EqualTo(1));
    }

    /// <summary>
    /// Disposing the timer is the whole reason <see cref="RtcDataChannelAdapter"/> is disposable:
    /// one 20ms timer per dialled connection, left running for the lifetime of the process, is the
    /// cost of forgetting. The <c>_closed</c> flag already makes a late tick inert, so no observable
    /// behavior distinguishes a disposed timer from a live one -- hence the reach into the field.
    /// <see cref="Timer.Change(int, int)"/> returning false is how a disposed timer answers.
    /// </summary>
    [Test]
    public void Close_DisposesThePollTimer()
    {
        RtcDataChannelAdapter adapter = new(DetachedChannel());
        Timer poll = (Timer)typeof(RtcDataChannelAdapter)
            .GetField("_bufferedAmountPoll", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(adapter)!;

        // Proves the field holds a live timer, so the assertion after Close() is about disposal
        // rather than about having found nothing.
        Assert.That(poll.Change(Timeout.Infinite, Timeout.Infinite), Is.True);

        adapter.Close();

        Assert.That(poll.Change(Timeout.Infinite, Timeout.Infinite), Is.False,
            "Close() must dispose the poll timer; a live one outlives the connection it was polling.");
    }

    /// <summary>
    /// The peer closing the channel is the ordinary way a connection ends, and WebRtcStream only
    /// learns about it through <see cref="IWebRtcDataChannel.Closed"/>. Driven through SIPSorcery's
    /// own <c>close()</c> rather than the adapter's, so the <c>onclose</c> subscription itself is
    /// what is under test.
    /// </summary>
    [Test]
    public void PeerClose_EndsTheChannel()
    {
        RTCDataChannel channel = DetachedChannel();
        using RtcDataChannelAdapter adapter = new(channel);
        int closed = 0;
        adapter.Closed += () => Interlocked.Increment(ref closed);

        channel.close();

        Assert.That(closed, Is.EqualTo(1));
    }

    /// <summary>
    /// A send that cannot reach the transport means the channel is gone, so the seam reports it as
    /// an <see cref="IOException"/> and ends the channel rather than letting SIPSorcery's own
    /// failure escape into a stream that would keep writing. A detached channel has no transport,
    /// which is exactly that case.
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    public void SendAsync_OnADeadTransport_FailsAsIoAndEndsTheChannel(CancellationToken token)
    {
        using RtcDataChannelAdapter adapter = new(DetachedChannel());
        int closed = 0;
        adapter.Closed += () => Interlocked.Increment(ref closed);

        Assert.That(async () => await adapter.SendAsync(new byte[] { 1, 2, 3 }, token), Throws.TypeOf<IOException>());
        Assert.That(closed, Is.EqualTo(1));
    }

    /// <summary>
    /// An errored channel is not coming back and SIPSorcery does not follow <c>onerror</c> with
    /// <c>onclose</c>, so a stream told only about <c>onclose</c> waits forever on a peer that has
    /// gone.
    /// </summary>
    [Test]
    public void Error_EndsTheChannel()
    {
        RTCDataChannel channel = DetachedChannel();
        using RtcDataChannelAdapter adapter = new(channel);
        int closed = 0;
        adapter.Closed += () => Interlocked.Increment(ref closed);

        Invoke(channel, "SetError", ["transport gone"]);

        Assert.That(closed, Is.EqualTo(1));
    }

    /// <summary>
    /// RFC 8831 section 6.6 carries an empty message as one zero padding byte under an *_Empty
    /// PPID. Forwarding that byte would desynchronize frame reassembly in
    /// <see cref="WebRtcStream"/>, and RTCDataChannel.GotData does not strip it.
    /// </summary>
    [Test]
    public void EmptyPpidPaddingByte_IsNotForwarded()
    {
        RTCDataChannel channel = DetachedChannel();
        using RtcDataChannelAdapter adapter = new(channel);
        List<byte[]> received = [];
        adapter.MessageReceived += received.Add;

        GotData(channel, DataChannelPayloadProtocols.WebRTC_Binary_Empty, [0]);
        GotData(channel, DataChannelPayloadProtocols.WebRTC_String_Empty, [0]);
        GotData(channel, DataChannelPayloadProtocols.WebRTC_Binary, [1, 2, 3]);

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0], Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    private static void GotData(RTCDataChannel channel, DataChannelPayloadProtocols ppid, byte[] data)
        => Invoke(channel, "GotData", [(ushort)0, (ushort)0, (uint)ppid, data]);

    /// <summary>
    /// <c>GotData</c> (the SCTP receive path's entry point) and <c>SetError</c> are both internal
    /// to SIPSorcery, so there is no way to drive <c>onmessage</c> or <c>onerror</c> from outside
    /// other than reflection. A <see cref="MissingMethodException"/> here means the member moved
    /// in a SIPSorcery upgrade -- which is the signal to recheck the adapter's handlers against
    /// the new assembly, not to delete the test.
    /// </summary>
    private static void Invoke(RTCDataChannel channel, string method, object?[] arguments)
    {
        System.Reflection.MethodInfo target = typeof(RTCDataChannel).GetMethod(
            method,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new MissingMethodException(nameof(RTCDataChannel), method);
        target.Invoke(channel, arguments);
    }
}
