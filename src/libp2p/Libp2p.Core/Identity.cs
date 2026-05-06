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
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Crypto.Generators;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/peer-ids/peer-ids.md
/// </summary>
public class Identity
{
    private const KeyType DefaultKeyType = KeyType.Ed25519;

    public PublicKey PublicKey { get; }
    public PrivateKey? PrivateKey { get; }

    public Identity(byte[]? privateKey = default, KeyType keyType = DefaultKeyType)
    {
        if (privateKey is null)
        {
            (PrivateKey, PublicKey) = GeneratePrivateKeyPair(keyType);
        }
        else
        {
            PrivateKey = new PrivateKey { Data = ByteString.CopyFrom(privateKey), Type = keyType };
            PublicKey = GetPublicKey(PrivateKey);
        }
    }

    public Identity(PrivateKey privateKey)
    {
        PrivateKey = privateKey;
        PublicKey = GetPublicKey(PrivateKey);
    }

    private (PrivateKey, PublicKey) GeneratePrivateKeyPair(KeyType type)
    {
        ByteString privateKeyData;
        ByteString? publicKeyData = null;
        switch (type)
        {
            case KeyType.Ed25519:
                {
                    Span<byte> privateKeyBytes = stackalloc byte[Ed25519.SecretKeySize];
                    SecureRandom rnd = new();
                    Ed25519.GeneratePrivateKey(rnd, privateKeyBytes);
                    privateKeyData = ByteString.CopyFrom(privateKeyBytes);
                }
                break;
            case KeyType.Rsa:
                {
                    using RSA rsa = RSA.Create(1024);
                    privateKeyData = ByteString.CopyFrom(rsa.ExportRSAPrivateKey());
                }
                break;
            case KeyType.Secp256K1:
                {
                    X9ECParameters curve = ECNamedCurveTable.GetByName("secp256k1");
                    ECDomainParameters domainParams = new(curve);

                    SecureRandom secureRandom = new();
                    ECKeyGenerationParameters keyParams = new(domainParams, secureRandom);

                    ECKeyPairGenerator generator = new("ECDSA");
                    generator.Init(keyParams);
                    AsymmetricCipherKeyPair keyPair = generator.GenerateKeyPair();
                    Span<byte> privateKeySpan = stackalloc byte[32];
                    ((ECPrivateKeyParameters)keyPair.Private).D.ToByteArrayUnsigned(privateKeySpan);
                    privateKeyData = ByteString.CopyFrom(privateKeySpan);
                    publicKeyData = ByteString.CopyFrom(((ECPublicKeyParameters)keyPair.Public).Q.GetEncoded(true));
                }
                break;
            case KeyType.Ecdsa:
                {
                    using ECDsa rsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    privateKeyData = ByteString.CopyFrom(rsa.ExportECPrivateKey());
                }
                break;
            default:
                throw new NotImplementedException($"{type} generation is not supported");
        }

        PrivateKey privateKey = new() { Type = type, Data = privateKeyData };
        return (privateKey, publicKeyData is not null ? new PublicKey { Type = type, Data = publicKeyData } : GetPublicKey(privateKey));
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
                    using RSA rsa = RSA.Create();
                    rsa.ImportRSAPrivateKey(privateKey.Data.Span, out int bytesRead);
                    publicKeyData = ByteString.CopyFrom(rsa.ExportSubjectPublicKeyInfo());
                }
                break;

            case KeyType.Secp256K1:
                {
                    X9ECParameters curve = CustomNamedCurves.GetByName("secp256k1");
                    ECPoint pointQ = curve.G.Multiply(new BigInteger(privateKey.Data.ToArray()));
                    publicKeyData = ByteString.CopyFrom(pointQ.GetEncoded(true));
                }
                break;

            case KeyType.Ecdsa:
                {
                    using ECDsa ecdsa = ECDsa.Create();
                    ecdsa.ImportECPrivateKey(privateKey.Data.Span, out int _);
                    publicKeyData = ByteString.CopyFrom(ecdsa.ExportSubjectPublicKeyInfo());
                }
                break;
            default:
                throw new NotImplementedException($"{privateKey.Type} is not supported");
        }

        return new() { Type = privateKey.Type, Data = publicKeyData };
    }

    public bool VerifySignature(byte[] message, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(PublicKey);

        switch (PublicKey.Type)
        {
            case KeyType.Ed25519:
                {
                    return Ed25519.Verify(signature, 0, PublicKey.Data.ToByteArray(), 0, message, 0, message.Length);
                }
            case KeyType.Rsa:
                {
                    using RSA rsa = RSA.Create();
                    rsa.ImportSubjectPublicKeyInfo(PublicKey.Data.Span, out _);

                    return rsa.VerifyData(message, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            case KeyType.Secp256K1:
                {
                    X9ECParameters curve = CustomNamedCurves.GetByName("secp256k1");
                    ISigner signer = SignerUtilities.GetSigner("SHA-256withECDSA");

                    ECPublicKeyParameters publicKeyParameters = new(
                            "ECDSA",
                            curve.Curve.DecodePoint(PublicKey.Data.ToArray()),
                            new ECDomainParameters(curve)
                            );

                    signer.Init(false, publicKeyParameters);
                    signer.BlockUpdate(message, 0, message.Length);
                    return signer.VerifySignature(signature);
                }
            case KeyType.Ecdsa:
                {
                    using ECDsa ecdsa = ECDsa.Create();
                    ecdsa.ImportSubjectPublicKeyInfo(PublicKey.Data.Span, out _);
                    return ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                }
            default:
                throw new NotImplementedException($"{PublicKey.Type} is not supported");
        }
    }

    public byte[] Sign(byte[] message)
    {
        if (PrivateKey is null)
        {
            throw new ArgumentException(nameof(PrivateKey));
        }

        switch (PublicKey.Type)
        {
            case KeyType.Ed25519:
                {
                    byte[] sig = new byte[Ed25519.SignatureSize];
                    Ed25519.Sign(PrivateKey.Data.ToByteArray(), 0, PublicKey.Data.ToByteArray(), 0,
                        message, 0, message.Length, sig, 0);
                    return sig;
                }
            case KeyType.Ecdsa:
                {
                    ECDsa e = ECDsa.Create();
                    e.ImportECPrivateKey(PrivateKey.Data.Span, out _);
                    return e.SignData(message, HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence);
                }
            case KeyType.Rsa:
                {
                    using RSA rsa = RSA.Create();
                    rsa.ImportRSAPrivateKey(PrivateKey.Data.Span, out _);
                    return rsa.SignData(message, 0, message.Length, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            case KeyType.Secp256K1:
                {
                    X9ECParameters curve = CustomNamedCurves.GetByName("secp256k1");
                    ISigner signer = SignerUtilities.GetSigner("SHA-256withECDSA");

                    ECPrivateKeyParameters privateKeyParams = new(
                        "ECDSA",
                        new BigInteger(1, PrivateKey.Data.ToArray()),
                        new ECDomainParameters(curve)
                        );

                    signer.Init(true, privateKeyParams);
                    signer.BlockUpdate(message, 0, message.Length);
                    return signer.GenerateSignature();
                }
            default:
                throw new NotImplementedException($"{PublicKey.Type} is not supported");
        }
    }

    public PeerId PeerId => new(PublicKey);
}
