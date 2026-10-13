// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.EC;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Core;

public static class PublicKeyExtensions
{
    /// <summary>
    /// Throw unless the key declares a known type and carries material that
    /// parses as a key of that type. A key that only looks well formed is
    /// still unusable: verification later throws instead of returning false.
    /// </summary>
    public static void EnsureUsable(this PublicKey publicKey)
    {
        if (!publicKey.HasType || !Enum.IsDefined(publicKey.Type) || publicKey.Data.IsEmpty)
        {
            throw new Libp2pException("Public key is malformed.");
        }

        try
        {
            switch (publicKey.Type)
            {
                case KeyType.Ed25519:
                    if (publicKey.Data.Length != Ed25519.PublicKeySize)
                    {
                        throw new Libp2pException(
                            $"Ed25519 public key must be {Ed25519.PublicKeySize} bytes, got {publicKey.Data.Length}.");
                    }
                    break;
                case KeyType.Secp256K1:
                    X9ECParameters curve = CustomNamedCurves.GetByName("secp256k1");
                    curve.Curve.DecodePoint(publicKey.Data.ToByteArray());
                    break;
                case KeyType.Rsa:
                    using (RSA rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKey.Data.Span, out _);
                    }
                    break;
                case KeyType.Ecdsa:
                    using (ECDsa ecdsa = ECDsa.Create())
                    {
                        ecdsa.ImportSubjectPublicKeyInfo(publicKey.Data.Span, out _);
                    }
                    break;
            }
        }
        catch (Exception e) when (e is not Libp2pException)
        {
            throw new Libp2pException($"Public key does not hold usable {publicKey.Type} material.", e);
        }
    }
}
