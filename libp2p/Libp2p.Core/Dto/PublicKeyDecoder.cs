// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Exceptions;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Formats.Asn1;
using System.Security.Cryptography;
using ECPoint = Org.BouncyCastle.Math.EC.ECPoint;

namespace Nethermind.Libp2p.Core.Dto;

/// <summary>
/// Turns wire bytes into a <see cref="PublicKey"/>, decoding and validating
/// together so a caller cannot hold a key that parsed but cannot be used.
/// It is the only entry point for key material that arrives from a remote peer.
/// </summary>
public static class PublicKeyDecoder
{
    private const int Secp256k1CompressedLength = 33;
    private const int Secp256k1UncompressedLength = 65;

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

        if (publicKey.HasType && publicKey.Type == KeyType.Rsa)
        {
            CheckRemoteRsaLimits(publicKey.Data);
        }
        PublicKey canonical = Normalize(publicKey, out int rsaKeyBits);
        if (canonical.Type == KeyType.Rsa && rsaKeyBits < 2048)
        {
            throw new Libp2pException($"RSA public key must be at least 2048 bits, got {rsaKeyBits}.");
        }
        return canonical;
    }

    private static void CheckRemoteRsaLimits(ByteString data)
    {
        // Inspect DER views before native import: a peer must not select unbounded
        // modulus or exponent sizes for any later import or signature verification.
        try
        {
            AsnReader outer = new(data.Memory, AsnEncodingRules.DER);
            AsnReader subjectPublicKeyInfo = outer.ReadSequence();
            subjectPublicKeyInfo.ReadSequence(); // The native importer validates the algorithm identifier.
            if (!subjectPublicKeyInfo.TryReadPrimitiveBitString(out int unusedBits, out ReadOnlyMemory<byte> encodedKey) || unusedBits != 0)
            {
                throw new Libp2pException("RSA public key has invalid bit-string encoding.");
            }
            subjectPublicKeyInfo.ThrowIfNotEmpty();
            outer.ThrowIfNotEmpty();
            AsnReader keyReader = new(encodedKey, AsnEncodingRules.DER);
            AsnReader key = keyReader.ReadSequence();
            ReadOnlySpan<byte> modulus = key.ReadIntegerBytes().Span;
            if ((modulus[0] & 0x80) != 0)
            {
                throw new Libp2pException("RSA public modulus must be positive.");
            }
            if (modulus[0] == 0) modulus = modulus[1..];
            if (modulus.Length > 8192 / 8)
            {
                throw new Libp2pException("RSA public modulus must be at most 8192 bits.");
            }
            if (!key.TryReadInt32(out int exponent) || exponent < 2)
            {
                throw new Libp2pException("RSA public exponent must be between 2 and 2147483647.");
            }
            key.ThrowIfNotEmpty();
            keyReader.ThrowIfNotEmpty();
        }
        catch (AsnContentException e)
        {
            throw new Libp2pException("RSA public key is not valid DER.", e);
        }
    }

    /// <summary>Returns a canonical copy of a local public key without applying remote admission policy.</summary>
    /// <remarks>
    /// Use <see cref="Decode"/> for remote key material. Local serialization preserves
    /// imported RSA identities below the remote size floor and does not change the caller's key.
    /// </remarks>
    /// <exception cref="Libp2pException">The key does not carry usable material in a supported format.</exception>
    public static PublicKey Normalize(PublicKey publicKey) => Normalize(publicKey, out _);

    internal static PublicKey Normalize(PublicKey publicKey, out int rsaKeyBits)
    {
        rsaKeyBits = 0;
        // Protobuf does not enforce proto2 required fields, so a key with no
        // type, or a type outside the enum, parses successfully.
        if (!publicKey.HasType || !Enum.IsDefined(publicKey.Type))
        {
            throw new Libp2pException("Public key does not declare a known key type.");
        }

        ByteString data = publicKey.Data;
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
                    data = CompressSecp256k1(data);
                    break;
                case KeyType.Rsa:
                    using (RSA rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKey.Data.Span, out int rsaBytesRead);
                        RejectTrailingBytes(publicKey, rsaBytesRead);
                        rsaKeyBits = checked((int)new System.Numerics.BigInteger(
                            rsa.ExportParameters(false).Modulus!, isUnsigned: true, isBigEndian: true).GetBitLength());
                        data = ByteString.CopyFrom(rsa.ExportSubjectPublicKeyInfo());
                    }
                    break;
                case KeyType.Ecdsa:
                    using (ECDsa ecdsa = ECDsa.Create())
                    {
                        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Data.Span, out int ecdsaBytesRead);
                        RejectTrailingBytes(publicKey, ecdsaBytesRead);
                        // Re-export preserves explicit parameters, allowing another ID for the same key.
                        if (!ecdsa.ExportParameters(false).Curve.IsNamed)
                        {
                            throw new Libp2pException("ECDSA public key must use a named curve.");
                        }
                        data = ByteString.CopyFrom(ecdsa.ExportSubjectPublicKeyInfo());
                    }
                    break;
            }
        }
        catch (Exception e) when (e is not Libp2pException and not OutOfMemoryException)
        {
            throw new Libp2pException($"Public key does not hold usable {publicKey.Type} material.", e);
        }

        // Libp2p key serialization contains exactly Type and Data, in tag order.
        return new PublicKey { Type = publicKey.Type, Data = data };
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
    /// on the curve and that a hybrid prefix matches the parity of y.
    /// </remarks>
    private static ByteString CompressSecp256k1(ByteString data)
    {
        // Only the two SEC1 point sizes can hold a key, so anything else is
        // rejected before decoding. That also rules out the one-byte encoding
        // of the point at infinity, which BouncyCastle would otherwise decode.
        if (data.Length is not (Secp256k1CompressedLength or Secp256k1UncompressedLength))
        {
            throw new Libp2pException(
                $"Secp256k1 public key must be {Secp256k1CompressedLength} or {Secp256k1UncompressedLength} bytes, got {data.Length}.");
        }

        ECPoint point = Secp256k1.Curve.DecodePoint(data.ToByteArray());

        // A compressed point that decodes re-encodes to the same bytes.
        return data.Length == Secp256k1CompressedLength ? data : ByteString.CopyFrom(point.GetEncoded(true));
    }
}
