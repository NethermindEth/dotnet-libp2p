// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Protocols.WebRtc.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcFramingTests
{
    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    [TestCase(Message.Types.Flag.Fin)]
    [TestCase(Message.Types.Flag.StopSending)]
    [TestCase(Message.Types.Flag.ResetStream)]
    [TestCase(Message.Types.Flag.FinAck)]
    public void RoundTrips_EveryFlag(Message.Types.Flag flag)
    {
        byte[] payload = Payload(64);
        byte[] frame = WebRtcFraming.Encode(flag, payload);

        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out int consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(message!.HasFlag, Is.True);
        Assert.That(message!.Flag, Is.EqualTo(flag));
        Assert.That(message!.Message_.ToByteArray(), Is.EqualTo(payload));
    }

    [Test]
    public void RoundTrips_FlagWithoutPayload()
    {
        byte[] frame = WebRtcFraming.Encode(Message.Types.Flag.FinAck, null);

        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out int consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(message!.HasFlag, Is.True);
        Assert.That(message!.Flag, Is.EqualTo(Message.Types.Flag.FinAck));
        Assert.That(message!.HasMessage_, Is.False);
    }

    [Test]
    public void RoundTrips_PayloadWithoutFlag()
    {
        byte[] payload = Payload(10);
        byte[] frame = WebRtcFraming.Encode(null, payload);

        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out int consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(message!.HasFlag, Is.False);
        Assert.That(message!.Message_.ToByteArray(), Is.EqualTo(payload));
    }

    [TestCase(true, 16377)]
    [TestCase(false, 16379)]
    public void LargestPayload_FillsTheFrameExactly(bool hasFlag, int expectedMaxPayload)
    {
        int maxPayload = WebRtcFraming.MaxPayloadSize(hasFlag);
        Assert.That(maxPayload, Is.EqualTo(expectedMaxPayload));

        Message.Types.Flag? flag = hasFlag ? Message.Types.Flag.Fin : null;
        byte[] frame = WebRtcFraming.Encode(flag, Payload(maxPayload));

        Assert.That(frame.Length, Is.LessThanOrEqualTo(WebRtcFraming.MaxFrameSize));
        Assert.That(frame.Length, Is.EqualTo(WebRtcFraming.MaxFrameSize));
        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out _), Is.True);
        Assert.That(message!.Message_.Length, Is.EqualTo(maxPayload));

        Assert.That(() => WebRtcFraming.Encode(flag, Payload(maxPayload + 1)),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void IncompleteFrame_NeedsMoreBytes()
    {
        byte[] frame = WebRtcFraming.Encode(Message.Types.Flag.ResetStream, Payload(500));
        int half = frame.Length / 2;

        Assert.That(WebRtcFraming.TryDecode(frame.AsSpan(0, half), out Message? partial, out int consumed), Is.False);
        Assert.That(partial, Is.Null);
        Assert.That(consumed, Is.Zero);

        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(message!.Message_.Length, Is.EqualTo(500));
    }

    [Test]
    public void TruncatedLengthPrefix_NeedsMoreBytes()
    {
        Assert.That(WebRtcFraming.TryDecode([], out _, out _), Is.False);
        Assert.That(WebRtcFraming.TryDecode([0x80], out _, out _), Is.False);
    }

    [Test]
    public void DeclaredLengthOverTheCap_IsRejected()
    {
        byte[] declares16384 = [0x80, 0x80, 0x01];

        Assert.That(() => WebRtcFraming.TryDecode(declares16384, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void DeclaredLengthExactlyFillingTheCap_AwaitsMoreBytes()
    {
        // 16382 body + 2 byte prefix == 16384, the largest legal frame.
        byte[] declares16382 = [0xFE, 0x7F];

        Assert.That(WebRtcFraming.TryDecode(declares16382, out _, out _), Is.False);
    }

    [Test]
    public void DeclaredLengthOneBytePastTheCap_IsRejected()
    {
        // 16383 body + 2 byte prefix == 16385. One byte over, so the guard must be a strict
        // comparison against the remaining budget rather than against MaxFrameSize itself.
        byte[] declares16383 = [0xFF, 0x7F];

        Assert.That(() => WebRtcFraming.TryDecode(declares16383, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void NineContinuationBytesWithNoTerminator_IsRejected()
    {
        // No legal varint continues past 9 bytes, so this is garbage the moment the ninth
        // continuation byte arrives. Treating it as "incomplete" instead would let a peer hold
        // the receive buffer open indefinitely by never sending a terminator.
        byte[] nineContinuationBytes = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80];

        Assert.That(() => WebRtcFraming.TryDecode(nineContinuationBytes, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void OverLongLengthPrefix_IsRejected()
    {
        // Ten bytes terminating in a NON-zero byte, so the minimal-encoding rule cannot be what
        // rejects it -- only the 9 byte unsigned-varint cap can.
        byte[] tenByteVarInt = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01];

        Assert.That(() => WebRtcFraming.TryDecode(tenByteVarInt, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void NonMinimalLengthPrefix_IsRejected()
    {
        // "The only number that can end in a 0x00 is 0": these pad a small value into a
        // longer encoding, which a decoder must reject rather than silently accept.
        byte[] paddedZero = [0x80, 0x00];
        byte[] paddedOne = [0x81, 0x00];

        Assert.That(() => WebRtcFraming.TryDecode(paddedZero, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
        Assert.That(() => WebRtcFraming.TryDecode(paddedOne, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [Test]
    public void MinimalZeroLengthPrefix_IsAccepted()
    {
        // A single 0x00 is the one legal encoding that ends in zero: an empty message.
        Assert.That(WebRtcFraming.TryDecode([0x00], out Message? message, out int consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(1));
        Assert.That(message!.HasFlag, Is.False);
        Assert.That(message!.HasMessage_, Is.False);
    }

    [Test]
    public void MalformedBody_SurfacesAsInvalidData()
    {
        // Tag 0 is never valid protobuf. A receive loop catches InvalidDataException, so the
        // underlying InvalidProtocolBufferException must not escape.
        byte[] frameWithZeroTag = [0x02, 0x00, 0x00];

        Assert.That(() => WebRtcFraming.TryDecode(frameWithZeroTag, out _, out _),
            Throws.InstanceOf<InvalidDataException>());
    }

    [TestCase(125, 128)]
    [TestCase(126, 130)]
    [TestCase(127, 131)]
    [TestCase(128, 133)]
    [TestCase(16379, 16384)]
    public void LengthPrefixGrowsAtTheVarIntBoundary(int payloadLength, int expectedFrameLength)
    {
        byte[] frame = WebRtcFraming.Encode(null, Payload(payloadLength));

        Assert.That(frame.Length, Is.EqualTo(expectedFrameLength));
        Assert.That(WebRtcFraming.TryDecode(frame, out Message? message, out int consumed), Is.True);
        Assert.That(consumed, Is.EqualTo(frame.Length));
        Assert.That(message!.Message_.Length, Is.EqualTo(payloadLength));
    }

    [TestCase(16380)]
    [TestCase(16383)]
    [TestCase(16384)]
    public void PayloadPastTheBoundary_IsRejected(int payloadLength)
    {
        Assert.That(() => WebRtcFraming.Encode(null, Payload(payloadLength)),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }
}
