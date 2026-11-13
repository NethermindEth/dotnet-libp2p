// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Noise.Dto;
using Nethermind.Libp2p.Protocols.WebRtc.Internals;
using Noise;
using System.Security.Cryptography;
using System.Text;
using PublicKey = Nethermind.Libp2p.Core.Dto.PublicKey;

namespace Nethermind.Libp2p.Protocols.WebRtc.Tests;

[TestFixture]
public class WebRtcDirectNoiseHandshakeTests
{
    private const string PayloadSigPrefix = "noise-libp2p-static-key:";

    /// <summary>Noise XX message 1 is "-> e": one X25519 public key, no static key, no payload.</summary>
    private const int NoiseMessageOneLength = 32;

    /// <summary>A side that is waiting for a peer that will never speak must fail the test rather
    /// than wedge the runner: with the Noise roles inverted both halves of a self-dial read first
    /// and nothing ever completes.</summary>
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a side that must not speak is watched before believing it never will.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(250);

    [Test]
    public async Task HandshakeAsync_AuthenticatesRemoteIdentity()
    {
        Channel initiatorChannel = new();
        IChannel responderChannel = initiatorChannel.Reverse;
        Identity initiator = new();
        Identity responder = new();
        byte[] prologue = Encoding.UTF8.GetBytes("test-prologue");

        Task<PublicKey> initiatorTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            initiatorChannel,
            initiator,
            prologue,
            isInitiator: true,
            CancellationToken.None);
        Task<PublicKey> responderTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            responderChannel,
            responder,
            prologue,
            isInitiator: false,
            CancellationToken.None);

        PublicKey initiatorRemoteKey = await initiatorTask.WaitAsync(HandshakeTimeout);
        PublicKey responderRemoteKey = await responderTask.WaitAsync(HandshakeTimeout);

        Assert.That(new Identity(initiatorRemoteKey).PeerId, Is.EqualTo(responder.PeerId));
        Assert.That(new Identity(responderRemoteKey).PeerId, Is.EqualTo(initiator.PeerId));
    }

    /// <summary>
    /// The oracle here is the Noise specification's own message size, not this code: whoever is
    /// the initiator writes exactly 32 bytes before reading anything, and whoever is the responder
    /// writes nothing at all until it has read them.
    /// <para>
    /// <see cref="HandshakeAsync_AuthenticatesRemoteIdentity"/> cannot catch a swapped role,
    /// because a self-dial gets both halves wrong together and the two errors cancel -- which is
    /// exactly how the inverted Noise role in this transport survived. Running one side against a
    /// peer that answers nothing removes that cancellation: either the side we called the
    /// initiator speaks first, or nothing moves at all.
    /// </para>
    /// </summary>
    [Test]
    public async Task Initiator_WritesNoiseMessageOne_BeforeReadingAnything()
    {
        byte[] prologue = Encoding.UTF8.GetBytes("webrtc-initiator-speaks-first");
        using CancellationTokenSource abandoned = new();

        Channel initiatorSide = new();
        IChannel silentResponder = initiatorSide.Reverse;
        Task initiatorHandshake = WebRtcDirectNoiseHandshake.HandshakeAsync(
            initiatorSide, new Identity(), prologue, isInitiator: true, abandoned.Token);

        ReadResult header = await silentResponder.ReadAsync(2).AsTask().WaitAsync(HandshakeTimeout);
        Assert.That(header.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(
            BinaryPrimitives.ReadUInt16BigEndian(header.Data.ToArray()),
            Is.EqualTo(NoiseMessageOneLength),
            "Noise XX message 1 is a bare ephemeral key.");
        Assert.That((await silentResponder.ReadAsync(NoiseMessageOneLength).AsTask().WaitAsync(HandshakeTimeout)).Result,
            Is.EqualTo(IOResult.Ok));

        Channel responderSide = new();
        IChannel silentInitiator = responderSide.Reverse;
        Task responderHandshake = WebRtcDirectNoiseHandshake.HandshakeAsync(
            responderSide, new Identity(), prologue, isInitiator: false, abandoned.Token);

        Task<ReadResult> secondFrame = silentResponder.ReadAsync(1).AsTask();
        Task<ReadResult> anythingAtAll = silentInitiator.ReadAsync(1).AsTask();
        await Task.Delay(QuietPeriod);

        Assert.That(secondFrame.IsCompleted, Is.False, "The initiator must stop after message 1 and read.");
        Assert.That(anythingAtAll.IsCompleted, Is.False, "The responder must write nothing before reading message 1.");

        await abandoned.CancelAsync();
        await ObserveAsync(initiatorHandshake);
        await ObserveAsync(responderHandshake);
    }

    [Test]
    public void ParseAndValidateRemoteKey_RejectsIdentitySignatureForDifferentKey()
    {
        KeyPair staticKey = KeyPair.Generate();
        Identity claimedIdentity = new();
        Identity signer = new();
        byte[] sigInput = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. staticKey.PublicKey];
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = claimedIdentity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(signer.Sign(sigInput)),
        };
        byte[] payloadBytes = payload.ToByteArray();

        Assert.That(
            () => WebRtcDirectNoiseHandshake.ParseAndValidateRemoteKey(payloadBytes, payloadBytes.Length, staticKey.PublicKey),
            Throws.TypeOf<CryptographicException>());
    }

    /// <remarks>
    /// The key used to escape as whatever <see cref="Identity.VerifySignature"/> threw;
    /// it must fail as the handshake's own signature error.
    /// </remarks>
    [Test]
    public void ParseAndValidateRemoteKey_RejectsUnusableIdentityKey()
    {
        KeyPair staticKey = KeyPair.Generate();
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = new PublicKey { Type = (Core.Dto.KeyType)7, Data = ByteString.CopyFrom([1, 2, 3]) }.ToByteString(),
            IdentitySig = ByteString.CopyFrom(new byte[64]),
        };
        byte[] payloadBytes = payload.ToByteArray();

        Assert.That(
            () => WebRtcDirectNoiseHandshake.ParseAndValidateRemoteKey(payloadBytes, payloadBytes.Length, staticKey.PublicKey),
            Throws.TypeOf<CryptographicException>());
    }

    /// <remarks>
    /// The padded key verifies the owner's signature over the static key, so only the
    /// key check stops the handshake from authenticating a second peer ID.
    /// </remarks>
    [Test]
    public void ParseAndValidateRemoteKey_RejectsPaddedEd25519IdentityKey()
    {
        KeyPair staticKey = KeyPair.Generate();
        Identity owner = new();
        byte[] sigInput = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. staticKey.PublicKey];
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = new PublicKey { Type = Core.Dto.KeyType.Ed25519, Data = ByteString.CopyFrom([.. owner.PublicKey.Data.ToByteArray(), 0x00]) }.ToByteString(),
            IdentitySig = ByteString.CopyFrom(owner.Sign(sigInput)),
        };
        byte[] payloadBytes = payload.ToByteArray();

        Assert.That(
            () => WebRtcDirectNoiseHandshake.ParseAndValidateRemoteKey(payloadBytes, payloadBytes.Length, staticKey.PublicKey),
            Throws.TypeOf<CryptographicException>());
    }

    /// <summary>A handshake left waiting on a peer that never answers faults when cancelled -- the
    /// channel reports the cancelled read as <see cref="IOResult.Cancelled"/>, which the handshake
    /// raises as <see cref="InvalidOperationException"/>. That fault is the point of the test and
    /// must not resurface later as an unobserved exception.</summary>
    private static async Task ObserveAsync(Task handshake)
    {
        try
        {
            await handshake;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
        }
    }
}
