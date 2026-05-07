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
        string? opaque = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(hostname);
        ArgumentException.ThrowIfNullOrEmpty(serverChallenge);

        byte[] clientChallenge = RandomNumberGenerator.GetBytes(32);
        string clientChallengeB64 = Convert.ToBase64String(clientChallenge);

        byte[] payload = BuildSignaturePayload(hostname, serverChallenge, clientChallengeB64);
        byte[] signature = identity.Sign(payload);

        string publicKeyB64 = Convert.ToBase64String(identity.PublicKey.ToByteArray());
        string sigB64 = Convert.ToBase64String(signature);

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
    /// Format: length-prefixed UTF-8 strings: "challenge-client=<b64>", "hostname=<host>", "challenge-server=<b64>".
    /// </summary>
    private static byte[] BuildSignaturePayload(string hostname, string serverChallenge, string clientChallenge)
    {
        // Sorted keys per spec for determinism.
        string[] parts = new[]
        {
            "challenge-server=" + clientChallenge,
            "challenge-client=" + serverChallenge,
            "hostname=" + hostname,
        };
        Array.Sort(parts, StringComparer.Ordinal);

        using MemoryStream ms = new();
        foreach (string p in parts)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(p);
            // 1-byte length prefix (parts are well under 256 bytes).
            ms.WriteByte(checked((byte)bytes.Length));
            ms.Write(bytes);
        }
        return ms.ToArray();
    }
}
