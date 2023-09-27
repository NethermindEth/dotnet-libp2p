// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Math;
using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;
using System.Security.Cryptography;
using System.Buffers;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/peer-ids/peer-ids.md
/// </summary>
public class Identity
{
    public PublicKey PublicKey { get; }
    public PrivateKey? PrivateKey { get; }

    public Identity(byte[]? privateKey = default, KeyType keyType = KeyType.Ed25519)
        : this(privateKey is null ? null : new PrivateKey { Data = ByteString.CopyFrom(privateKey), Type = keyType })
    {
    }

    public Identity(PrivateKey? privateKey)
    {
        if (privateKey is null)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(Ed25519.SecretKeySize);
            Span<byte> privateKeyBytesSpan = rented.AsSpan(0, Ed25519.SecretKeySize);
            SecureRandom rnd = new();
            Ed25519.GeneratePrivateKey(rnd, privateKeyBytesSpan);
            ArrayPool<byte>.Shared.Return(rented, true);
            privateKey = new PrivateKey { Data = ByteString.CopyFrom(privateKeyBytesSpan), Type = KeyType.Ed25519 };
        }
        PrivateKey = privateKey;
        PublicKey = GetPublicKey(privateKey);

    }

    public Identity(PublicKey publicKey)
    {
        PublicKey = publicKey;
    }

    private static PublicKey GetPublicKey(PrivateKey privateKey)
    {
        ByteString publicKeyData;
        switch (privateKey.Type)
        {
            case KeyType.Ed25519:
                {
                    byte[] rented = ArrayPool<byte>.Shared.Rent(Ed25519.SecretKeySize);
                    Span<byte> publicKeyBytesSpan = rented.AsSpan(0, Ed25519.SecretKeySize);
                    Ed25519.GeneratePublicKey(privateKey.Data.Span, publicKeyBytesSpan);
                    publicKeyData = ByteString.CopyFrom(publicKeyBytesSpan);
                    ArrayPool<byte>.Shared.Return(rented, true);
                }
                break;

            case KeyType.Rsa:
                {
                    RSA rsa = RSA.Create();
                    rsa.ImportRSAPrivateKey(privateKey.Data.Span, out int bytesRead);
                    publicKeyData = ByteString.CopyFrom(rsa.ExportSubjectPublicKeyInfo());
                }
                break;

            case KeyType.Secp256K1:
                {
                    X9ECParameters curve = ECNamedCurveTable.GetByName("secp256k1");
                    Org.BouncyCastle.Math.EC.ECPoint pointQ
                        = curve.G.Multiply(new BigInteger(1, privateKey.Data.Span));
                    publicKeyData = ByteString.CopyFrom(pointQ.GetEncoded(true));
                }
                break;

            case KeyType.Ecdsa:
                {
                    ECDsa rsa = ECDsa.Create();
                    rsa.ImportECPrivateKey(privateKey.Data.Span, out int _);
                    publicKeyData = ByteString.CopyFrom(rsa.ExportSubjectPublicKeyInfo());
                }
                break;
            default:
                throw new NotImplementedException($"{privateKey.Type} is not supported");
        }

        return new() { Type = privateKey.Type, Data = publicKeyData };
    }

    public PeerId PeerId => new(PublicKey);
}
