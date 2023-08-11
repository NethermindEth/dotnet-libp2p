// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

extern alias BouncyCastleCryptography;
using BouncyCastleCryptography::Org.BouncyCastle.Asn1.X9;
using BouncyCastleCryptography::Org.BouncyCastle.Math;
using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using BouncyCastleCryptography::Org.BouncyCastle.Math.EC.Rfc8032;
using BouncyCastleCryptography::Org.BouncyCastle.Security;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/peer-ids/peer-ids.md
///     Ed25519 > RSA > Secp256k1,ECDSA
/// </summary>
public class Identity
{
    public Identity(byte[]? privateKey = null, KeyType keyType = KeyType.Ed25519)
    {
        if (privateKey == null)
        {
            privateKey = new byte[32];
            SecureRandom rnd = new();
            Ed25519.GeneratePrivateKey(rnd, privateKey);
        }

        byte[]? publicKey = null;
        switch (keyType)
        {
            case KeyType.Ed25519:
                publicKey = new byte[32];
                Ed25519.GeneratePublicKey(privateKey, 0, publicKey, 0);
                break;

            case KeyType.Secp256K1:
                X9ECParameters curve = ECNamedCurveTable.GetByName("secp256k1");
                BouncyCastleCryptography::Org.BouncyCastle.Math.EC.ECPoint pointQ
                    = curve.G.Multiply(new BigInteger(1, privateKey));
                publicKey = pointQ.GetEncoded(true);
                break;
        }
        PrivateKey = privateKey;
        PublicKey = new PublicKey { Type = keyType, Data = ByteString.CopyFrom(publicKey) };
    }

    private Identity(PublicKey publicKey)
    {
        PublicKey = publicKey;
    }

    public PublicKey PublicKey { get; }
    public byte[] PrivateKey { get; }

    public string PeerId => new PeerId(PublicKey).ToString();
    public byte[] PeerIdBytes => new PeerId(PublicKey).Bytes;

    public static Identity FromPrivateKey(byte[] privateKey)
    {
        return new Identity(privateKey);
    }

    public static Identity FromPublicKey(byte[] publicKey)
    {
        PublicKey? pubKey = PublicKey.Parser.ParseFrom(publicKey);
        return new Identity(pubKey);
    }
}
