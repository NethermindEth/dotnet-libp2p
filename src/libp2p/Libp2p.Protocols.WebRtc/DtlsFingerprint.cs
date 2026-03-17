// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Hash;
using SIPSorcery.Net;
using System.Security.Cryptography;

namespace Nethermind.Libp2p.Protocols.WebRtc;

/// <summary>
/// Represents a DTLS certificate fingerprint as it appears in multiaddrs (/certhash)
/// and in SDP (a=fingerprint:).
/// </summary>
public record DtlsFingerprint(string Algorithm, byte[] Value)
{
    public static DtlsFingerprint FromRtcFingerprint(RTCDtlsFingerprint fingerprint)
    {
        string? algorithm = GetPropertyString(fingerprint, "algorithm") ?? GetPropertyString(fingerprint, "Algorithm");
        string? value = GetPropertyString(fingerprint, "value") ?? GetPropertyString(fingerprint, "Value");

        if (string.IsNullOrWhiteSpace(algorithm) || string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException("RTCDtlsFingerprint is missing algorithm or value.");
        }

        return ParseFromSdp($"{algorithm} {value}");
    }

    public static DtlsFingerprint ParseFromSdp(string sdpFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdpFingerprint);

        string[] parts = sdpFingerprint.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new FormatException($"Invalid SDP fingerprint format: {sdpFingerprint}");
        }

        string normalizedHex = parts[1].Replace(":", string.Empty, StringComparison.Ordinal);
        return new DtlsFingerprint(parts[0].ToLowerInvariant(), Convert.FromHexString(normalizedHex));
    }

    public static DtlsFingerprint ParseFromMultihash(ReadOnlySpan<byte> multihashBytes)
    {
        if (multihashBytes.Length == 0)
        {
            throw new FormatException("Multihash bytes are empty.");
        }

        Multihash multihash = Multihash.Decode(multihashBytes.ToArray());
        if (multihash.Code != HashType.SHA2_256)
        {
            throw new FormatException($"Unsupported fingerprint multihash code: {multihash.Code}");
        }

        return new DtlsFingerprint("sha-256", multihash.Digest);
    }

    public byte[] ToMultihashBytes()
    {
        if (!Algorithm.Equals("sha-256", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Unsupported DTLS fingerprint algorithm: {Algorithm}");
        }

        return Multihash.Encode(Value, HashType.SHA2_256);
    }

    public string ToSdpString()
    {
        string hexWithColon = string.Join(':', Convert.ToHexString(Value).Chunk(2).Select(c => new string(c)));
        return $"{Algorithm.ToLowerInvariant()} {hexWithColon}";
    }

    public bool Matches(RTCDtlsFingerprint sdpFingerprint)
    {
        DtlsFingerprint parsed = FromRtcFingerprint(sdpFingerprint);
        return Algorithm.Equals(parsed.Algorithm, StringComparison.OrdinalIgnoreCase) &&
               CryptographicOperations.FixedTimeEquals(Value, parsed.Value);
    }

    private static string? GetPropertyString(object source, string propertyName)
    {
        Type sourceType = source.GetType();
        object? propertyValue = sourceType.GetProperty(propertyName)?.GetValue(source);
        if (propertyValue is not null)
        {
            return propertyValue.ToString();
        }

        object? fieldValue = sourceType.GetField(propertyName)?.GetValue(source);
        return fieldValue?.ToString();
    }
}