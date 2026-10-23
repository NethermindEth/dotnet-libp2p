// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using SIPSorcery.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

/// <summary>
/// The parts of <see cref="RtcDataChannelAdapter"/> that do not need a live peer connection: the
/// synthesised BufferedAmountLow event, and that every way the channel can end collapses into one
/// <see cref="IWebRtcDataChannel.Closed"/> and one disposed timer. A detached
/// <see cref="RTCDataChannel"/> is enough -- it reports <c>bufferedAmount</c> 0 and closes
/// without a transport.
/// </summary>
[TestFixture]
public class RtcDataChannelAdapterTests
{
    private static RTCDataChannel DetachedChannel() => new(null!, new RTCDataChannelInit { negotiated = true, id = 0 });

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
    /// PPID. Forwarding that byte would desynchronise frame reassembly in
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
