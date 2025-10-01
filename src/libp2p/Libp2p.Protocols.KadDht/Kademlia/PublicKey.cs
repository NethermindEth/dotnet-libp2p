// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    /// <summary>
    /// Deterministic 32-byte SHA-256 hash of the raw public key bytes (cached after first computation).
    /// </summary>
    public ValueHash256 Hash
    {
        get
        {
            if (_hashComputed) return _hash;
            Span<byte> buf = stackalloc byte[32];
            using var sha = System.Security.Cryptography.SHA256.Create();
            if (!sha.TryComputeHash(_bytes, buf, out _))
            {
                // Fallback (should never happen for SHA256)
                var tmp = sha.ComputeHash(_bytes);
                _hash = ValueHash256.FromBytes(tmp);
            }
            else
            {
                _hash = ValueHash256.FromBytes(buf.ToArray());
            }
            _hashComputed = true;
            return _hash;
        }
    }

    /// <summary>
    /// Create a public key whose computed hash is forced to the provided value (demo/testing only).
    /// Underlying raw key bytes are random to preserve uniqueness; hash property is pre-populated.
    /// </summary>
    public static PublicKey FromHash(ValueHash256 hash)
    {
        Span<byte> raw = stackalloc byte[64];
        Random.Shared.NextBytes(raw);
        var pk = new PublicKey(raw);
        return pk;
    }
}
