// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Formats.Cbor;
using System.Text;
using Google.Protobuf;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2P.Protocols.KadDht.Dto;

namespace Libp2p.Protocols.KadDht.Storage;

/// <summary>
/// Helper for creating and parsing properly signed IPNS v2 records.
/// <para>
/// IPNS records are the value stored in the DHT under <c>/ipns/{PeerId}</c> keys.
/// They contain a content path (e.g., <c>/ipfs/Qm...</c>), a monotonically increasing
/// sequence number, a TTL hint, and a validity period (EOL timestamp).
/// </para>
/// <para>
/// v2 records use CBOR-encoded data + domain-separated signatures, compatible with
/// go-libp2p, js-libp2p, rust-libp2p, and py-libp2p.
/// </para>
/// </summary>
public static class IpnsRecordHelper
{
    /// <summary>
    /// The domain separation prefix for IPNS v2 signature.
    /// Per the IPNS spec: <c>signing_input = "ipns-signature:" || data</c>
    /// </summary>
    private static readonly byte[] SigningDomain = "ipns-signature:"u8.ToArray();

    /// <summary>
    /// Builds the DHT key for an IPNS record: <c>/ipns/{PeerId-multihash}</c>.
    /// </summary>
    /// <param name="peerId">The PeerId of the IPNS record owner.</param>
    /// <returns>The DHT key bytes.</returns>
    public static byte[] BuildIpnsKey(PeerId peerId)
    {
        ArgumentNullException.ThrowIfNull(peerId);
        byte[] prefix = IpnsRecordValidator.Prefix;
        byte[] key = new byte[prefix.Length + peerId.Bytes.Length];
        Buffer.BlockCopy(prefix, 0, key, 0, prefix.Length);
        Buffer.BlockCopy(peerId.Bytes, 0, key, prefix.Length, peerId.Bytes.Length);
        return key;
    }

    /// <summary>
    /// Creates a signed IPNS v2 record ready to be stored in the DHT via <c>PutValueAsync</c>.
    /// <para>
    /// The record is signed with both v1 (legacy) and v2 signatures for maximum
    /// interoperability with all libp2p implementations.
    /// </para>
    /// </summary>
    /// <param name="identity">The identity (with private key) of the record author.</param>
    /// <param name="contentPath">The content path this IPNS record points to (e.g., <c>/ipfs/Qm...</c>).</param>
    /// <param name="sequence">Monotonically increasing sequence number. Higher wins.</param>
    /// <param name="eol">End-of-life timestamp after which the record is expired.</param>
    /// <param name="ttl">Cache TTL hint for resolvers (how long to cache before re-resolving).</param>
    /// <returns>Serialized <see cref="IpnsEntry"/> protobuf bytes suitable for DHT PUT_VALUE.</returns>
    public static byte[] CreateSignedRecord(
        Identity identity,
        string contentPath,
        ulong sequence,
        DateTimeOffset eol,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(contentPath);

        if (identity.PrivateKey is null)
            throw new InvalidOperationException("Cannot sign IPNS records without a private key.");

        byte[] valueBytes = Encoding.UTF8.GetBytes(contentPath);
        byte[] validityBytes = Encoding.UTF8.GetBytes(eol.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.000000000Z"));
        ulong ttlNs = ttl.HasValue
            ? (ulong)ttl.Value.Ticks * 100 // 1 tick = 100ns
            : 3_600_000_000_000; // default 1 hour in nanoseconds

        // Build the CBOR-encoded data field (v2)
        byte[] cborData = EncodeCborData(valueBytes, validityBytes, sequence, ttlNs);

        // Build v2 signature: Sign("ipns-signature:" || cborData)
        byte[] sigV2Input = new byte[SigningDomain.Length + cborData.Length];
        Buffer.BlockCopy(SigningDomain, 0, sigV2Input, 0, SigningDomain.Length);
        Buffer.BlockCopy(cborData, 0, sigV2Input, SigningDomain.Length, cborData.Length);
        byte[] signatureV2 = identity.Sign(sigV2Input);

        // Build v1 signature (legacy): Sign(value || validity || "EOL")
        // This maintains backward compatibility with older implementations
        byte[] eolTag = "EOL"u8.ToArray();
        byte[] sigV1Input = new byte[valueBytes.Length + validityBytes.Length + eolTag.Length];
        Buffer.BlockCopy(valueBytes, 0, sigV1Input, 0, valueBytes.Length);
        Buffer.BlockCopy(validityBytes, 0, sigV1Input, valueBytes.Length, validityBytes.Length);
        Buffer.BlockCopy(eolTag, 0, sigV1Input, valueBytes.Length + validityBytes.Length, eolTag.Length);
        byte[] signatureV1 = identity.Sign(sigV1Input);

        var entry = new IpnsEntry
        {
            Value = ByteString.CopyFrom(valueBytes),
            ValidityType = IpnsEntry.Types.ValidityType.Eol,
            Validity = ByteString.CopyFrom(validityBytes),
            Sequence = sequence,
            Ttl = ttlNs,
            SignatureV1 = ByteString.CopyFrom(signatureV1),
            SignatureV2 = ByteString.CopyFrom(signatureV2),
            Data = ByteString.CopyFrom(cborData),
            PubKey = identity.PublicKey.ToByteString()
        };

        return entry.ToByteArray();
    }

    /// <summary>
    /// Encode the CBOR data field for IPNS v2 per the spec.
    /// <para>
    /// CBOR map with text string keys: { "Value", "Validity", "ValidityType", "Sequence", "TTL" }
    /// </para>
    /// </summary>
    private static byte[] EncodeCborData(byte[] value, byte[] validity, ulong sequence, ulong ttlNs)
    {
        var writer = new CborWriter(CborConformanceMode.Canonical);
        writer.WriteStartMap(5);

        // Keys must be sorted by length then lexicographically for canonical CBOR (DAG-CBOR)
        // Sorted: "TTL" (3), "Value" (5), "Sequence" (8), "Validity" (8), "ValidityType" (12)
        writer.WriteTextString("TTL");
        writer.WriteByteString(BitConverter.GetBytes(ttlNs));

        writer.WriteTextString("Value");
        writer.WriteByteString(value);

        writer.WriteTextString("Sequence");
        writer.WriteByteString(BitConverter.GetBytes(sequence));

        writer.WriteTextString("Validity");
        writer.WriteByteString(validity);

        writer.WriteTextString("ValidityType");
        writer.WriteByteString([(byte)IpnsEntry.Types.ValidityType.Eol]);

        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>
    /// Parse an IPNS record value to extract the content path.
    /// </summary>
    /// <param name="recordValue">The raw value bytes from a DHT GET_VALUE response.</param>
    /// <returns>The content path string, or null if parsing fails.</returns>
    public static string? GetContentPath(ReadOnlySpan<byte> recordValue)
    {
        try
        {
            var entry = IpnsEntry.Parser.ParseFrom(recordValue);
            return entry.HasValue ? Encoding.UTF8.GetString(entry.Value.Span) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse an IPNS record value to extract the sequence number.
    /// </summary>
    public static ulong? GetSequence(ReadOnlySpan<byte> recordValue)
    {
        try
        {
            var entry = IpnsEntry.Parser.ParseFrom(recordValue);
            return entry.HasSequence ? entry.Sequence : null;
        }
        catch
        {
            return null;
        }
    }
}
