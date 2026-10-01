// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;

namespace Libp2p.Protocols.KadDht.Kademlia;

public class PublicKey
{
    private readonly byte[] _bytes;
    private bool _hashComputed;
    private ValueHash256 _hash;

    public PublicKey(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public static ValueHash256 ComputeHash(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[ValueHash256.HashLength];
        SHA256.HashData(bytes, hash);
        return ValueHash256.FromBytes(hash);
    }

    /// <summary>
    /// Deterministic 32-byte SHA-256 hash of the public key bytes (cached after first computation).
    /// </summary>
    public ValueHash256 Hash
    {
        get
        {
            if (_hashComputed) return _hash;
            _hash = ComputeHash(_bytes);
            _hashComputed = true;
            return _hash;
        }
    }

}
