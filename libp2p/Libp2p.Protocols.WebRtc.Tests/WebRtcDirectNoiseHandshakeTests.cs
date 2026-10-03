// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
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

    [Test]
    public async Task HandshakeAsync_AuthenticatesRemoteIdentity()
    {
        Channel initiatorChannel = new();
        IChannel responderChannel = initiatorChannel.Reverse;
        Identity initiator = new();
        Identity responder = new();
        byte[] prologue = Encoding.UTF8.GetBytes("test-prologue");

        Task<(IChannel Encrypted, PublicKey RemoteKey)> initiatorTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            initiatorChannel,
            initiator,
            prologue,
            isInitiator: true,
            CancellationToken.None);
        Task<(IChannel Encrypted, PublicKey RemoteKey)> responderTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            responderChannel,
            responder,
            prologue,
            isInitiator: false,
            CancellationToken.None);

        (IChannel initiatorEncrypted, PublicKey initiatorRemoteKey) = await initiatorTask;
        (IChannel responderEncrypted, PublicKey responderRemoteKey) = await responderTask;

        Assert.That(new Identity(initiatorRemoteKey).PeerId, Is.EqualTo(responder.PeerId));
        Assert.That(new Identity(responderRemoteKey).PeerId, Is.EqualTo(initiator.PeerId));

        await initiatorEncrypted.CloseAsync();
        await responderEncrypted.CloseAsync();
    }

    [Test]
    public async Task AbortingEncryptedChannelAfterHalfClosePropagatesAbort()
    {
        Channel inner = new();
        IChannel otherSide = inner.Reverse;
        byte[] prologue = Encoding.UTF8.GetBytes("webrtc-abort-test");
        Task<(IChannel Encrypted, PublicKey RemoteKey)> initiatorTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            inner, new Identity(), prologue, isInitiator: true, CancellationToken.None);
        Task<(IChannel Encrypted, PublicKey RemoteKey)> responderTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            otherSide, new Identity(), prologue, isInitiator: false, CancellationToken.None);

        (IChannel encrypted, _) = await initiatorTask;
        (IChannel responderEncrypted, _) = await responderTask;
        Task<IOResult> incoming = responderEncrypted.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 5, 6 })).AsTask();
        Assert.That((await encrypted.ReadAsync(1)).Data.ToArray(), Is.EqualTo(new byte[] { 5 }));
        Assert.That(await incoming, Is.EqualTo(IOResult.Ok));
        Assert.That(await encrypted.WriteEofAsync(), Is.EqualTo(IOResult.Ok));
        await encrypted.AbortAsync();

        Assert.That((await encrypted.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
        Assert.That(await encrypted.WriteEofAsync(), Is.EqualTo(IOResult.Aborted));
        Assert.That((await otherSide.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [Test]
    public async Task AbortDuringEncryptedFrameReadRemainsAborted()
    {
        Channel inner = new();
        IChannel otherSide = inner.Reverse;
        byte[] prologue = Encoding.UTF8.GetBytes("webrtc-frame-abort-test");
        Task<(IChannel Encrypted, PublicKey RemoteKey)> initiatorTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            inner, new Identity(), prologue, isInitiator: true, CancellationToken.None);
        Task<(IChannel Encrypted, PublicKey RemoteKey)> responderTask = WebRtcDirectNoiseHandshake.HandshakeAsync(
            otherSide, new Identity(), prologue, isInitiator: false, CancellationToken.None);

        (IChannel encrypted, _) = await initiatorTask;
        await responderTask;
        Task<ReadResult> pending = encrypted.ReadAsync(1).AsTask();
        Assert.That(await otherSide.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 0, 10 })), Is.EqualTo(IOResult.Ok));
        await otherSide.AbortAsync();

        Assert.That((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Result, Is.EqualTo(IOResult.Aborted));
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
}
