// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Dto;
using Multiformats.Hash;
using SimpleBase;
using Google.Protobuf;
using Multiformats.Base;
using Nethermind.Libp2p.Core.Enums;
using Multihash = Multiformats.Hash.Multihash;

namespace Nethermind.Libp2p.Core;

public class PeerId : IComparable<PeerId>, IComparable
{
    public readonly byte[] Bytes;
    int? hashCode = null;

    public PeerId(PublicKey publicKey)
    {
        byte[] serializedPublicKey = publicKey.ToByteArray();

        if (serializedPublicKey.Length <= 42)
        {
            Bytes = Multihash.Encode(serializedPublicKey, HashType.ID);
        }
        else
        {
            Bytes = Multihash.Sum(HashType.SHA2_256, serializedPublicKey);
        }
    }

    public static PublicKey? ExtractPublicKey(byte[] peerId)
    {
        Multihash multihash = Multihash.Decode(peerId);
        if (multihash.Code != HashType.ID)
        {
            return null;
        }
        PublicKey? pubKey = PublicKey.Parser.ParseFrom(multihash.Digest);
        if (pubKey is null || pubKey.Type != KeyType.Ed25519 || multihash.Code != HashType.ID)
        {
            return null;
        }

        return pubKey;
    }

    public PeerId(string peerId)
    {
        if (peerId.StartsWith("bafz"))
        {
            byte[] peerIdBytes = Multibase.Decode(peerId, out MultibaseEncoding encoding);
            byte cidVersion = peerIdBytes[0];
            if ((Cid)cidVersion != Cid.Cidv1)
            {
                throw new NotImplementedException($"CIDs of version {cidVersion} are not supported");
            }
            int offset = 1 * sizeof(byte);

            Ipld multicodec = (Ipld)VarInt.Decode(peerIdBytes.AsSpan(), ref offset);
            if (multicodec != Ipld.Libp2pKey)
            {
                throw new Exception("Invalid encoding of peerId");
            }
            Bytes = peerIdBytes[offset..];
        }
        else
        {
            Bytes = Base58.Bitcoin.Decode(peerId);
        }
    }

    public PeerId(byte[] bytes)
    {
        Bytes = bytes;
    }

    public override string ToString() => Base58.Bitcoin.Encode(Bytes);

    public string ToCidString()
    {
        // CID format: <multibase><cid-version><multicodec><multihash>
        // Calculate required buffer size
        int codecSize = VarInt.GetSizeInBytes((int)Ipld.Libp2pKey);
        byte[] encodedPeerIdBytes = new byte[1 + codecSize + Bytes.Length];

        int offset = 0;
        encodedPeerIdBytes[offset++] = (byte)Cid.Cidv1;
        VarInt.Encode((int)Ipld.Libp2pKey, encodedPeerIdBytes, ref offset);
        Array.Copy(Bytes, 0, encodedPeerIdBytes, offset, Bytes.Length);

        return Multibase.Encode(MultibaseEncoding.Base32Lower, encodedPeerIdBytes);
    }

    public static implicit operator PeerId(string peerId)
    {
        return new PeerId(peerId);
    }

    #region Equality
    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(this, obj))
        {
            return true;
        }

        if (obj is not PeerId peerId)
        {
            return false;
        }

        return Bytes.SequenceEqual(peerId.Bytes);
    }

    public override int GetHashCode()
    {
        static int ComputeHash(params byte[] data)
        {
            unchecked
            {
                const int p = 16777619;
                int hash = (int)2166136261;

                for (int i = 0; i < data.Length; i++)
                    hash = (hash ^ data[i]) * p;

                return hash;
            }
        }

        return hashCode ??= ComputeHash(Bytes);
    }

    public int CompareTo(PeerId? other) => other is null ? 1 : Bytes.AsSpan().SequenceCompareTo(other.Bytes.AsSpan());

    public int CompareTo(object? obj) => obj is not PeerId other ? 1 : CompareTo(other);

    public static bool operator ==(PeerId? left, PeerId? right)
    {
        if (left is null)
        {
            if (right is null)
            {
                return true;
            }

            return false;
        }
        return left.Equals(right);
    }

    public static bool operator !=(PeerId? left, PeerId? right) => !(left == right);
    #endregion
}
