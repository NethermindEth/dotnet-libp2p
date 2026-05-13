// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using NoiseHandshakePayload = Nethermind.Libp2p.Protocols.Noise.Dto.NoiseHandshakePayload;
using Noise;
using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal static class WebRtcDirectNoiseHandshake
{
    private static readonly Protocol NoiseProt = new(HandshakePattern.XX, CipherFunction.ChaChaPoly, HashFunction.Sha256);
    private const string PayloadSigPrefix = "noise-libp2p-static-key:";

    public static async Task<(IChannel Encrypted, PublicKey RemoteKey)> HandshakeAsync(
        IChannel channel,
        Identity localIdentity,
        byte[] prologue,
        bool isInitiator,
        CancellationToken token)
    {
        KeyPair staticKey = KeyPair.Generate();
        using HandshakeState hs = NoiseProt.Create(isInitiator, prologue: prologue, s: staticKey.PrivateKey);

        PublicKey remoteKey;
        Transport transport;

        if (isInitiator)
        {
            byte[] msg0Buf = new byte[Protocol.MaxMessageLength];
            (int w0, _, _) = hs.WriteMessage(null, msg0Buf);
            await WriteFramedAsync(channel, msg0Buf, w0, token);

            byte[] msg1Raw = await ReadFramedAsync(channel, token);
            byte[] msg1Buf = new byte[Protocol.MaxMessageLength];
            (int r1, _, _) = hs.ReadMessage(msg1Raw, msg1Buf);
            remoteKey = ParseAndValidateRemoteKey(msg1Buf, r1, hs.RemoteStaticPublicKey);

            byte[] identityPayload = BuildIdentityPayload(localIdentity, staticKey.PublicKey);
            byte[] msg2Buf = new byte[Protocol.MaxMessageLength];
            (int w2, _, Transport t) = hs.WriteMessage(identityPayload, msg2Buf);
            await WriteFramedAsync(channel, msg2Buf, w2, token);
            transport = t;
        }
        else
        {
            byte[] msg0Raw = await ReadFramedAsync(channel, token);
            byte[] msg0Buf = new byte[Protocol.MaxMessageLength];
            hs.ReadMessage(msg0Raw, msg0Buf);

            byte[] identityPayload = BuildIdentityPayload(localIdentity, staticKey.PublicKey);
            byte[] msg1Buf = new byte[Protocol.MaxMessageLength];
            (int w1, _, _) = hs.WriteMessage(identityPayload, msg1Buf);
            await WriteFramedAsync(channel, msg1Buf, w1, token);

            byte[] msg2Raw = await ReadFramedAsync(channel, token);
            byte[] msg2Buf = new byte[Protocol.MaxMessageLength];
            (int r2, _, Transport t) = hs.ReadMessage(msg2Raw, msg2Buf);
            remoteKey = ParseAndValidateRemoteKey(msg2Buf, r2, hs.RemoteStaticPublicKey);
            transport = t;
        }

        return (new NoiseEncryptedChannel(channel, transport), remoteKey);
    }

    private static byte[] BuildIdentityPayload(Identity identity, ReadOnlySpan<byte> staticPublicKey)
    {
        byte[] sigInput = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. staticPublicKey.ToArray()];
        NoiseHandshakePayload payload = new()
        {
            IdentityKey = identity.PublicKey.ToByteString(),
            IdentitySig = ByteString.CopyFrom(identity.Sign(sigInput)),
        };
        return payload.ToByteArray();
    }

    internal static PublicKey ParseAndValidateRemoteKey(byte[] payloadBuffer, int payloadLength, ReadOnlySpan<byte> remoteStaticPublicKey)
    {
        if (payloadLength <= 0)
        {
            throw new CryptographicException("Noise handshake payload is empty.");
        }

        if (remoteStaticPublicKey.IsEmpty)
        {
            throw new CryptographicException("Noise handshake did not expose the remote static public key.");
        }

        NoiseHandshakePayload envelope = NoiseHandshakePayload.Parser.ParseFrom(payloadBuffer.AsSpan(0, payloadLength));
        PublicKey remoteKey = PublicKey.Parser.ParseFrom(envelope.IdentityKey);
        byte[] signature = envelope.IdentitySig.ToByteArray();
        if (signature.Length == 0)
        {
            throw new CryptographicException("Noise handshake identity signature is missing.");
        }

        Identity remoteIdentity = new(remoteKey);
        byte[] sigInput = [.. Encoding.UTF8.GetBytes(PayloadSigPrefix), .. remoteStaticPublicKey.ToArray()];
        if (!remoteIdentity.VerifySignature(sigInput, signature))
        {
            throw new CryptographicException("Noise handshake identity signature is invalid.");
        }

        return remoteKey;
    }

    private static async Task WriteFramedAsync(IChannel channel, byte[] buffer, int length, CancellationToken token)
    {
        if (length > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Noise handshake frame is too large: {length} bytes.");
        }

        byte[] frame = new byte[2 + length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)length);
        buffer.AsSpan(0, length).CopyTo(frame.AsSpan(2));
        IOResult result = await channel.WriteAsync(new ReadOnlySequence<byte>(frame), token);
        if (result != IOResult.Ok)
        {
            throw new InvalidOperationException($"Noise handshake write failed: {result}");
        }
    }

    private static async Task<byte[]> ReadFramedAsync(IChannel channel, CancellationToken token)
    {
        ReadResult lenResult = await channel.ReadAsync(2, ReadBlockingMode.WaitAll, token);
        if (lenResult.Result != IOResult.Ok)
            throw new InvalidOperationException($"Noise handshake read failed: {lenResult.Result}");

        int length = BinaryPrimitives.ReadUInt16BigEndian(lenResult.Data.ToArray());
        byte[] buf = new byte[length];
        int offset = 0;

        while (offset < length)
        {
            ReadResult chunk = await channel.ReadAsync(length - offset, ReadBlockingMode.WaitAll, token);
            if (chunk.Result != IOResult.Ok)
                throw new InvalidOperationException($"Noise handshake read failed: {chunk.Result}");
            byte[] bytes = chunk.Data.ToArray();
            bytes.CopyTo(buf, offset);
            offset += bytes.Length;
        }

        return buf;
    }
}
