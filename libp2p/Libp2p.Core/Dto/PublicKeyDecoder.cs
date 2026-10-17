// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Exceptions;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace Nethermind.Libp2p.Core.Dto;

/// <summary>
/// Turns wire bytes into a <see cref="PublicKey"/>, decoding and validating
/// together so a caller cannot hold a key that parsed but cannot be used.
/// Intended as the only entry point for untrusted material; the remaining
/// <c>PublicKey.Parser.ParseFrom</c> call sites still need migrating.
/// </summary>
public static class PublicKeyDecoder
{
    private const int Secp256k1CompressedLength = 33;

    private static readonly X9ECParameters Secp256k1 = CustomNamedCurves.GetByName("secp256k1");

    /// <summary>
    /// Decode a protobuf-encoded public key and verify it carries usable
    /// material for its declared type.
    /// </summary>
    /// <remarks>
    /// Secp256k1 keys come back in SEC1 compressed form whatever form they
    /// arrived in, so every encoding of one key derives the same peer ID.
    /// </remarks>
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
                    publicKey.Data = CompressSecp256k1(publicKey.Data);
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

    /// <summary>
    /// Re-encode a secp256k1 point in SEC1 compressed form, the form go-libp2p,
    /// rust-libp2p and js-libp2p all serialize before deriving a peer ID.
    /// </summary>
    /// <remarks>
    /// The peer ID hashes the serialized key, so the uncompressed and hybrid
    /// encodings of a point would each derive their own ID. Compressed is 33
    /// bytes, which keeps the serialized key under the 42-byte inlining limit;
    /// the 65-byte forms are hashed with SHA2-256 instead, so the IDs differ
    /// in multihash code as well as digest. BouncyCastle checks the point is
    /// on the curve and that a hybrid prefix matches the parity of y, but it
    /// also decodes a single zero byte as the point at infinity, which cannot
    /// verify anything.
    /// </remarks>
    private static ByteString CompressSecp256k1(ByteString data)
    {
        ECPoint point = Secp256k1.Curve.DecodePoint(data.ToByteArray());
        if (point.IsInfinity)
        {
            throw new Libp2pException("Secp256k1 public key is the point at infinity.");
        }

        // A compressed point that decodes re-encodes to the same bytes.
        return data.Length == Secp256k1CompressedLength ? data : ByteString.CopyFrom(point.GetEncoded(true));
    }
}
