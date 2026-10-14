// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Exceptions;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Core.Dto;

/// <summary>
/// Turns wire bytes into a <see cref="PublicKey"/>, decoding and validating
/// together so a caller cannot hold a key that parsed but cannot be used.
/// Intended as the only entry point for untrusted material; the remaining
/// <c>PublicKey.Parser.ParseFrom</c> call sites still need migrating.
/// </summary>
public static class PublicKeyDecoder
{
    /// <summary>
    /// Decode a protobuf-encoded public key and verify it carries usable
    /// material for its declared type.
    /// </summary>
    /// <exception cref="Libp2pException">The bytes are not a usable key.</exception>
    public static PublicKey Decode(ByteString bytes)
    {
        PublicKey publicKey;
        try
        {
            publicKey = PublicKey.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException e)
        {
            throw new Libp2pException("Public key is not valid protobuf.", e);
        }

        // Protobuf does not enforce proto2 required fields, so a key with no
        // type, or a type outside the enum, parses successfully.
        if (!publicKey.HasType || !Enum.IsDefined(publicKey.Type))
        {
            throw new Libp2pException("Public key does not declare a known key type.");
        }

        try
        {
            switch (publicKey.Type)
            {
                case KeyType.Ed25519:
                    // Decoding cannot catch this: Ed25519 verification reads the
                    // first 32 bytes and ignores any that follow, so a padded key
                    // verifies the same signatures under a different peer ID.
                    if (publicKey.Data.Length != Ed25519.PublicKeySize)
                    {
                        throw new Libp2pException(
                            $"Ed25519 public key must be {Ed25519.PublicKeySize} bytes, got {publicKey.Data.Length}.");
                    }
                    break;
                case KeyType.Secp256K1:
                    X9ECParameters curve = CustomNamedCurves.GetByName("secp256k1");
                    // A single zero byte is a valid encoding of the point at
                    // infinity, which decodes but cannot verify anything.
                    if (curve.Curve.DecodePoint(publicKey.Data.ToByteArray()).IsInfinity)
                    {
                        throw new Libp2pException("Secp256k1 public key is the point at infinity.");
                    }

                    break;
                case KeyType.Rsa:
                    // No key size bounds: identities generated here use 1024-bit
                    // keys, which go-libp2p's 2048 floor would reject.
                    using (RSA rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKey.Data.Span, out int rsaBytesRead);
                        RejectTrailingBytes(publicKey, rsaBytesRead);
                    }
                    break;
                case KeyType.Ecdsa:
                    using (ECDsa ecdsa = ECDsa.Create())
                    {
                        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Data.Span, out int ecdsaBytesRead);
                        RejectTrailingBytes(publicKey, ecdsaBytesRead);
                    }
                    break;
            }
        }
        catch (Exception e) when (e is not Libp2pException and not OutOfMemoryException)
        {
            throw new Libp2pException($"Public key does not hold usable {publicKey.Type} material.", e);
        }

        return publicKey;
    }

    /// <summary>
    /// Reject material that carries bytes past the key itself. The importers
    /// stop at the end of the DER structure, so anything after it would change
    /// the peer ID while leaving the key usable.
    /// </summary>
    private static void RejectTrailingBytes(PublicKey publicKey, int bytesRead)
    {
        if (bytesRead != publicKey.Data.Length)
        {
            throw new Libp2pException(
                $"{publicKey.Type} public key has {publicKey.Data.Length - bytesRead} trailing byte(s).");
        }
    }
}
