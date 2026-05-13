// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

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
