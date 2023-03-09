// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core.Dto;
using Org.BouncyCastle.Math.EC.Rfc8032;
using Org.BouncyCastle.Security;

namespace Nethermind.Libp2p.Core;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/peer-ids/peer-ids.md
///     Ed25519 > RSA > Secp256k1,ECDSA
/// </summary>
public class Identity
{
    private readonly byte[] _privateKey;

    public Identity(byte[]? privateKey = null)
    {
        if (privateKey == null)
        {
            privateKey = new byte[32];
            SecureRandom rnd = new();
            Ed25519.GeneratePrivateKey(rnd, privateKey);
        }

        byte[] publicKey = new byte[32];
        Ed25519.GeneratePublicKey(privateKey, publicKey);

        _privateKey = privateKey;
        PublicKey = new PublicKey { Type = KeyType.Ed25519, Data = ByteString.CopyFrom(publicKey) };
    }

    private Identity(PublicKey publicKey)
    {
        PublicKey = publicKey;
    }

    public PublicKey PublicKey { get; }

    public string PeerId => new RawPeerId(PublicKey).ToString();
    public byte[] PeerIdBytes => new RawPeerId(PublicKey).ToByteArray();

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
