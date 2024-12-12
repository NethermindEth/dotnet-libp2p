// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Dto;

public static class SigningHelper
{
    private static readonly byte[] PayloadType = [((ushort)Enums.Libp2p.Libp2pPeerRecord >> 8) & 0xFF, (ushort)Enums.Libp2p.Libp2pPeerRecord & 0xFF];
    private static readonly byte[] Domain = "libp2p-peer-record"u8.ToArray().ToArray();
    public static bool VerifyPeerRecord(ByteString signedEnvelopeBytes, PublicKey publicKey)
    {
        SignedEnvelope signedEnvelope = SignedEnvelope.Parser.ParseFrom(signedEnvelopeBytes);
        return VerifyPeerRecord(signedEnvelope, publicKey);
    }

    public static bool VerifyPeerRecord(SignedEnvelope signedEnvelope, PublicKey publicKey)
    {
        Identity identity = new(publicKey);

        if (signedEnvelope.PayloadType?.Take(2).SequenceEqual(PayloadType) is not true)
        {
            return false;
        }

        PeerRecord pr = PeerRecord.Parser.ParseFrom(signedEnvelope.Payload);

        if (identity.PeerId != new PeerId(pr.PeerId.ToByteArray()))
        {
            return false;
        }

        byte[] signedData = new byte[
            VarInt.GetSizeInBytes(Domain.Length) + Domain.Length +
            VarInt.GetSizeInBytes(PayloadType.Length) + PayloadType.Length +
            VarInt.GetSizeInBytes(signedEnvelope.Payload.Length) + signedEnvelope.Payload.Length];

        int offset = 0;

        VarInt.Encode(Domain.Length, signedData.AsSpan(), ref offset);
        Array.Copy(Domain, 0, signedData, offset, Domain.Length);
        offset += Domain.Length;

        VarInt.Encode(PayloadType.Length, signedData.AsSpan(), ref offset);
        Array.Copy(PayloadType, 0, signedData, offset, PayloadType.Length);
        offset += PayloadType.Length;

        VarInt.Encode(signedEnvelope.Payload.Length, signedData.AsSpan(), ref offset);
        Array.Copy(signedEnvelope.Payload.ToByteArray(), 0, signedData, offset, signedEnvelope.Payload.Length);

        return identity.VerifySignature(signedData, signedEnvelope.Signature.ToByteArray());
    }

    public static ByteString CreateSignedEnvelope(Identity identity, Multiaddress[] addresses, ulong seq)
    {
        PeerRecord payload = new()
        {
            PeerId = ByteString.CopyFrom(identity.PeerId.Bytes),
            Seq = seq
        };

        foreach (Multiaddress address in addresses)
        {
            payload.Addresses.Add(new AddressInfo
            {
                Multiaddr = ByteString.CopyFrom(address.ToBytes())
            });
        }

        SignedEnvelope envelope = new()
        {
            PayloadType = ByteString.CopyFrom(PayloadType),
            Payload = payload.ToByteString(),
            PublicKey = identity.PublicKey.ToByteString(),
        };

        int payloadLength = payload.CalculateSize();

        byte[] signingData = new byte[
            VarInt.GetSizeInBytes(Domain.Length) + Domain.Length +
            VarInt.GetSizeInBytes(PayloadType.Length) + PayloadType.Length +
            VarInt.GetSizeInBytes(payloadLength) + payloadLength];

        int offset = 0;

        VarInt.Encode(Domain.Length, signingData.AsSpan(), ref offset);
        Array.Copy(Domain, 0, signingData, offset, Domain.Length);
        offset += Domain.Length;

        VarInt.Encode(PayloadType.Length, signingData.AsSpan(), ref offset);
        Array.Copy(PayloadType, 0, signingData, offset, PayloadType.Length);
        offset += PayloadType.Length;

        VarInt.Encode(payloadLength, signingData.AsSpan(), ref offset);
        Array.Copy(payload.ToByteArray(), 0, signingData, offset, payloadLength);

        envelope.Signature = ByteString.CopyFrom(identity.Sign(signingData).ToArray());

        return envelope.ToByteString();
    }
}
