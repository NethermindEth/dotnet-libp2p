// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Nethermind.Libp2p.Core.Dto;
using Multiformats.Hash;
using SimpleBase;
using Google.Protobuf;

namespace Nethermind.Libp2p.Core;

public class PeerId
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
            Bytes = Multihash.Sum(HashType.SHA3_256, serializedPublicKey);
        }
    }

    public PeerId(string peerId)
    {
        Bytes = Base58.Bitcoin.Decode(peerId);
    }

    public PeerId(byte[] bytes)
    {
        Bytes = bytes;
    }

    public override string ToString()
    {
        return Base58.Bitcoin.Encode(Bytes);
    }

    public static implicit operator PeerId(string peerId)
    {
        return new PeerId(peerId);
    }

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

    public static bool operator ==(PeerId left, PeerId right)
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

    public static bool operator !=(PeerId left, PeerId right) => !(left == right);
}
