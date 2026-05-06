// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Nethermind.Libp2p.Core;
using System.Security.Cryptography;
using System.Text;

namespace Nethermind.Libp2p.Protocols.AutoTls.Internal;

/// <summary>
/// Minimal client-side helper for libp2p HTTP peer-id-auth.
/// Spec: https://github.com/libp2p/specs/blob/master/http/peer-id-auth.md
/// </summary>
internal static class PeerIdAuth
{
    private const string AuthScheme = "libp2p-PeerID";

    public static string BuildAuthorizationHeader(
        Identity identity,
        string hostname,
        string serverChallenge,
        string serverPublicKey,
        string? opaque = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(hostname);
        ArgumentException.ThrowIfNullOrEmpty(serverChallenge);
        ArgumentException.ThrowIfNullOrEmpty(serverPublicKey);

        byte[] clientChallenge = RandomNumberGenerator.GetBytes(32);
        string clientChallengeB64 = Base64UrlEncode(clientChallenge);

        byte[] serverPublicKeyBytes = Base64UrlDecode(serverPublicKey);
        byte[] payload = BuildSignaturePayload(hostname, serverChallenge, serverPublicKeyBytes);
        byte[] signature = identity.Sign(payload);

        string publicKeyB64 = Base64UrlEncode(identity.PublicKey.ToByteArray());
        string sigB64 = Base64UrlEncode(signature);

        StringBuilder sb = new();
        sb.Append(AuthScheme).Append(' ');
        sb.Append("public-key=\"").Append(publicKeyB64).Append('"');
        sb.Append(", challenge-server=\"").Append(clientChallengeB64).Append('"');
        sb.Append(", sig=\"").Append(sigB64).Append('"');
        if (!string.IsNullOrEmpty(opaque))
        {
            sb.Append(", opaque=\"").Append(opaque).Append('"');
        }
        return sb.ToString();
    }

    public static IReadOnlyDictionary<string, string> ParseChallenge(string? wwwAuthenticate)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(wwwAuthenticate))
        {
            return result;
        }

        int schemeIdx = wwwAuthenticate.IndexOf(AuthScheme, StringComparison.OrdinalIgnoreCase);
        ReadOnlySpan<char> rest = schemeIdx >= 0
            ? wwwAuthenticate.AsSpan(schemeIdx + AuthScheme.Length)
            : wwwAuthenticate.AsSpan();

        foreach (string part in rest.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            string key = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim().Trim('"');
            result[key] = value;
        }
        return result;
    }

    /// <summary>
    /// Build the canonical signature payload per peer-id-auth spec.
    /// Format: scheme prefix followed by sorted, uvarint length-prefixed fields.
    /// </summary>
    private static byte[] BuildSignaturePayload(string hostname, string serverChallenge, byte[] serverPublicKey)
    {
        (string Key, byte[] Value)[] parts =
        {
            ("challenge-client", Encoding.UTF8.GetBytes(serverChallenge)),
            ("hostname", Encoding.UTF8.GetBytes(hostname)),
            ("server-public-key", serverPublicKey),
        };
        Array.Sort(parts, static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        using MemoryStream ms = new();
        ms.Write(Encoding.ASCII.GetBytes(AuthScheme));
        foreach ((string key, byte[] value) in parts)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            WriteUVarInt(ms, (ulong)(keyBytes.Length + 1 + value.Length));
            ms.Write(keyBytes);
            ms.WriteByte((byte)'=');
            ms.Write(value);
        }
        return ms.ToArray();
    }

    private static void WriteUVarInt(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
        => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/'));
}
