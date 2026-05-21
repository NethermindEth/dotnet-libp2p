// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2P.Protocols.KadDht.Dto;
using Multiformats.Hash;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Validates IPNS records stored under the /ipns/ namespace.
/// <para>
/// Per the IPNS spec (https://specs.ipfs.tech/ipns/ipns-record/), IPNS v2 records
/// contain a <c>data</c> field (CBOR-encoded) and a <c>signatureV2</c> that signs
/// <c>"ipns-signature:" || data</c> using the author's private key.
/// </para>
/// <para>
/// The public key is either embedded in the record (<c>pubKey</c> field) or extracted
/// from the PeerId in the DHT key when the PeerId uses an identity multihash
/// (keys &lt;= 42 bytes, typically ed25519).
/// </para>
/// <para>
/// Selection picks the record with the highest sequence number (newest wins).
/// This is compatible with go-libp2p, js-libp2p, and rust-libp2p.
/// </para>
/// </summary>
public sealed class IpnsRecordValidator : IRecordValidator
{
    /// <summary>
    /// The key prefix for IPNS records per the libp2p DHT spec.
    /// </summary>
    public static readonly byte[] Prefix = "/ipns/"u8.ToArray();

    /// <summary>
    /// The domain separation string for IPNS v2 signature verification.
    /// Per the IPNS spec: signing_input = "ipns-signature:" || data
    /// </summary>
    private static readonly byte[] SigningDomain = "ipns-signature:"u8.ToArray();

    public static readonly IpnsRecordValidator Instance = new();

    /// <summary>
    /// Returns true if the given key starts with the /ipns/ prefix.
    /// </summary>
    public static bool MatchesPrefix(ReadOnlySpan<byte> key) =>
        key.Length > Prefix.Length && key[..Prefix.Length].SequenceEqual(Prefix);

    /// <summary>
    /// Validate an IPNS record: parse the protobuf, extract or derive the public key,
    /// then verify the v2 signature over the data field.
    /// <para>
    /// Key format: <c>/ipns/{PeerId-multihash}</c>
    /// Value: serialized <see cref="IpnsEntry"/> protobuf.
    /// </para>
    /// </summary>
    public bool Validate(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty) return false;
        if (!MatchesPrefix(key)) return false;

        ReadOnlySpan<byte> peerIdBytes = key[Prefix.Length..];
        if (peerIdBytes.IsEmpty) return false;

        IpnsEntry entry;
        try
        {
            entry = IpnsEntry.Parser.ParseFrom(value);
        }
        catch
        {
            return false;
        }

        // v2 records require both signatureV2 and data
        if (!entry.HasSignatureV2 || !entry.HasData)
        {
            // Fall back to v1 if present (legacy, less secure but still interoperable)
            return entry.HasSignatureV1 && entry.HasValue;
        }

        // Check EOL validity if present
        if (entry.HasValidityType &&
            entry.ValidityType == IpnsEntry.Types.ValidityType.Eol &&
            entry.HasValidity)
        {
            string eolString = Encoding.UTF8.GetString(entry.Validity.Span);
            if (DateTimeOffset.TryParse(eolString, out DateTimeOffset eol) &&
                eol < DateTimeOffset.UtcNow)
            {
                return false; // Expired
            }
        }

        // Extract the public key for signature verification
        PublicKey? publicKey = ExtractPublicKey(peerIdBytes, entry);
        if (publicKey is null)
        {
            return false;
        }

        // Verify the v2 signature: signing_input = "ipns-signature:" || data
        return VerifySignatureV2(publicKey, entry);
    }

    /// <summary>
    /// Select the IPNS record with the highest sequence number.
    /// Per the spec, higher sequence number wins; this is how all implementations resolve conflicts.
    /// </summary>
    public int Select(ReadOnlySpan<byte> key, IReadOnlyList<byte[]> values)
    {
        if (values.Count == 0) return -1;

        int bestIndex = -1;
        ulong bestSequence = 0;
        bool anyValid = false;

        for (int i = 0; i < values.Count; i++)
        {
            IpnsEntry entry;
            try
            {
                entry = IpnsEntry.Parser.ParseFrom(values[i]);
            }
            catch
            {
                continue;
            }

            if (!entry.HasSequence) continue;

            if (!anyValid || entry.Sequence > bestSequence)
            {
                bestSequence = entry.Sequence;
                bestIndex = i;
                anyValid = true;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Extract the public key from either the IPNS record or the PeerId multihash.
    /// <para>
    /// For ed25519 keys (identity multihash in PeerId), the key is extracted directly.
    /// For other key types, the record must contain the pubKey field.
    /// </para>
    /// </summary>
    private static PublicKey? ExtractPublicKey(ReadOnlySpan<byte> peerIdBytes, IpnsEntry entry)
    {
        // Try extracting from the PeerId (works for ed25519 identity multihash keys)
        PublicKey? fromPeerId = PeerId.ExtractPublicKey(peerIdBytes.ToArray());

        if (entry.HasPubKey && !entry.PubKey.IsEmpty)
        {
            try
            {
                PublicKey fromRecord = PublicKey.Parser.ParseFrom(entry.PubKey);

                // If we also got a key from the PeerId, they must match
                if (fromPeerId is not null)
                {
                    if (fromPeerId.Type != fromRecord.Type ||
                        !fromPeerId.Data.Span.SequenceEqual(fromRecord.Data.Span))
                    {
                        return null; // Mismatch — reject
                    }
                }

                // Verify the public key matches the PeerId
                var derivedPeerId = new PeerId(fromRecord);
                if (!derivedPeerId.Bytes.AsSpan().SequenceEqual(peerIdBytes))
                {
                    return null; // Public key doesn't match the IPNS key
                }

                return fromRecord;
            }
            catch
            {
                // Invalid public key protobuf
            }
        }

        // If the PeerId is an identity multihash, we already extracted the key
        if (fromPeerId is not null)
        {
            return fromPeerId;
        }

        // Cannot determine public key — record is invalid
        return null;
    }

    /// <summary>
    /// Verify the IPNS v2 signature.
    /// <para>
    /// Per the spec: <c>signing_input = "ipns-signature:" || data</c>
    /// where data is the raw bytes from the protobuf data field (CBOR-encoded).
    /// </para>
    /// </summary>
    private static bool VerifySignatureV2(PublicKey publicKey, IpnsEntry entry)
    {
        try
        {
            byte[] data = entry.Data.ToByteArray();
            byte[] signingInput = new byte[SigningDomain.Length + data.Length];
            Buffer.BlockCopy(SigningDomain, 0, signingInput, 0, SigningDomain.Length);
            Buffer.BlockCopy(data, 0, signingInput, SigningDomain.Length, data.Length);

            var identity = new Identity(publicKey);
            return identity.VerifySignature(signingInput, entry.SignatureV2.ToByteArray());
        }
        catch
        {
            return false;
        }
    }
}
