// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Validates public key records stored under the /pk/ namespace.
/// The value must be a public key whose SHA-256 hash matches the PeerId bytes.
/// </summary>
public sealed class PublicKeyRecordValidator : IRecordValidator
{
    /// <summary>
    /// The key prefix for public key records per the libp2p DHT spec.
    /// </summary>
    public static readonly byte[] Prefix = "/pk/"u8.ToArray();

    public static readonly PublicKeyRecordValidator Instance = new();

    /// <summary>
    /// Returns true if the given key starts with the /pk/ prefix.
    /// </summary>
    public static bool MatchesPrefix(ReadOnlySpan<byte> key) =>
        key.Length > Prefix.Length && key[..Prefix.Length].SequenceEqual(Prefix);

    /// <summary>
    /// Validate that the public key value hashes to the PeerId embedded in the key.
    /// Key format: /pk/{PeerId bytes}
    /// Validation: SHA-256(value) == PeerId bytes (after /pk/ prefix).
    /// </summary>
    public bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return false;
        if (!MatchesPrefix(key)) return false;

        ReadOnlySpan<byte> peerIdBytes = key[Prefix.Length..];
        if (peerIdBytes.IsEmpty) return false;

        Span<byte> hash = stackalloc byte[32];
        if (!SHA256.TryHashData(value, hash, out int written) || written != 32)
            return false;

        // The PeerId should be the SHA-256 hash of the public key.
        // Allow either exact match or prefix match (PeerId may be multihash-encoded).
        return hash.SequenceEqual(peerIdBytes) ||
               (peerIdBytes.Length > hash.Length && peerIdBytes[^hash.Length..].SequenceEqual(hash));
    }

    /// <summary>
    /// For public key records, there should be only one valid key per PeerId.
    /// Select the first valid record (they should all be identical if valid).
    /// </summary>
    public int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values)
    {
        if (values.Count == 0) return -1;

        // All valid public key records for the same PeerId should be identical.
        // Return the first valid one.
        for (int i = 0; i < values.Count; i++)
        {
            if (Validate(key, values[i]))
                return i;
        }

        return -1;
    }
}
