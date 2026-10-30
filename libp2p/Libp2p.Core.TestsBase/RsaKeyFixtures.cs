// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Core.TestsBase;

public static class RsaKeyFixtures
{
    // Public parameters need no expensive private-key generation for admission boundary tests.
    public static PublicKey PublicKey(int modulusBits, BigInteger exponent)
    {
        byte[] modulus = new byte[(modulusBits + 7) / 8];
        modulus[0] = (byte)(1 << ((modulusBits - 1) % 8));
        modulus[^1] |= 1;
        return Encode(new BigInteger(modulus, isUnsigned: true, isBigEndian: true), exponent);
    }

    public static PublicKey EquivalentLargeExponent(Identity signer)
    {
        using RSA rsa = RSA.Create();
        rsa.ImportRSAPrivateKey(signer.PrivateKey!.Data.Span, out _);
        RSAParameters key = rsa.ExportParameters(true);
        BigInteger Read(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);
        // e + phi(n) verifies the original key's signatures, while forcing a large exponent.
        return Encode(Read(key.Modulus!), Read(key.Exponent!) + (Read(key.P!) - 1) * (Read(key.Q!) - 1));
    }

    private static PublicKey Encode(BigInteger modulus, BigInteger exponent)
    {
        AsnWriter key = new(AsnEncodingRules.DER);
        using (key.PushSequence())
        {
            key.WriteInteger(modulus);
            key.WriteInteger(exponent);
        }
        AsnWriter subjectPublicKeyInfo = new(AsnEncodingRules.DER);
        using (subjectPublicKeyInfo.PushSequence())
        {
            using (subjectPublicKeyInfo.PushSequence())
            {
                subjectPublicKeyInfo.WriteObjectIdentifier("1.2.840.113549.1.1.1");
                subjectPublicKeyInfo.WriteNull();
            }
            subjectPublicKeyInfo.WriteBitString(key.Encode());
        }
        return new PublicKey { Type = KeyType.Rsa, Data = ByteString.CopyFrom(subjectPublicKeyInfo.Encode()) };
    }
}
