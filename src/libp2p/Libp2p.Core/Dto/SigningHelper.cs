// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Multiformats.Address;

namespace Nethermind.Libp2p.Core.Dto;

public static class SigningHelper
{
    private static readonly byte[] Libp2pPeerRecordAsArray = [((ushort)Enums.Libp2p.Libp2pPeerRecord >> 8) & 0xFF, (ushort)Enums.Libp2p.Libp2pPeerRecord & 0xFF];

    public static bool VerifyPeerRecord(ByteString signedEnvelopeBytes, PublicKey publicKey)
    {
        SignedEnvelope signedEnvelope = SignedEnvelope.Parser.ParseFrom(signedEnvelopeBytes);
        return VerifyPeerRecord(signedEnvelope, publicKey);
    }

    public static bool VerifyPeerRecord(SignedEnvelope signedEnvelope, PublicKey publicKey)
    {
        Identity identity = new(publicKey);

        if (signedEnvelope.PayloadType?.Take(2).SequenceEqual(Libp2pPeerRecordAsArray) is not true)
        {
            return false;
        }

        PeerRecord pr = PeerRecord.Parser.ParseFrom(signedEnvelope.Payload);

        if (identity.PeerId != new PeerId(pr.PeerId.ToByteArray()))
        {
            return false;
        }

        SignedEnvelope envelopeWithoutSignature = signedEnvelope.Clone();
        envelopeWithoutSignature.ClearSignature();

        return identity.VerifySignature(envelopeWithoutSignature.ToByteArray(), signedEnvelope.Signature.ToByteArray());
    }

    public static ByteString CreateSignedEnvelope(Identity identity, Multiaddress[] addresses, ulong seq)
    {
        PeerRecord paylaod = new()
        {
            PeerId = ByteString.CopyFrom(identity.PeerId.Bytes),
            Seq = seq
        };

        foreach (Multiaddress address in addresses)
        {
            paylaod.Addresses.Add(new AddressInfo
            {
                Multiaddr = ByteString.CopyFrom(address.ToBytes())
            });
        }

        SignedEnvelope envelope = new()
        {
            PayloadType = ByteString.CopyFrom(Libp2pPeerRecordAsArray),
            Payload = paylaod.ToByteString(),
            PublicKey = identity.PublicKey.ToByteString(),
        };

        envelope.Signature = ByteString.CopyFrom(identity.Sign(envelope.ToByteArray()));
        return envelope.ToByteString();
    }
}
