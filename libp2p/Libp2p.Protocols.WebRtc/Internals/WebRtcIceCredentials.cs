// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal enum WebRtcDirectIceFlow
{
    Unknown = 0,
    V1,
    V2,
}

/// <summary>
/// The ICE credentials a listener reads out of an incoming STUN binding request's USERNAME attribute,
/// as specified by the webrtc-direct spec: "_B_ splits the <c>USERNAME</c> on the first <c>:</c> into
/// <c>server_ufrag</c> and <c>client_ufrag</c>, and MUST check that both are well-formed ICE username
/// fragments (the <c>ice-char</c> set and length bounds of RFC 8839, section 5.4)".
/// </summary>
internal readonly record struct WebRtcIceCredentials(string ServerUfrag, string ClientUfrag, WebRtcDirectIceFlow Flow)
{
    public const string V1Prefix = "libp2p+webrtc+v1/";
    public const string V2Prefix = "libp2p+webrtc+v2/";

    public const int MinUfragLength = 4;
    public const int MaxUfragLength = 256;
    public const int MinPasswordLength = 22;
    /// <summary>
    /// The prefix counts against the ufrag limit, so a password longer than this can never
    /// produce a legal v2 ufrag. Derived rather than written out so the two cannot drift.
    /// </summary>
    public static readonly int MaxPasswordLength = MaxUfragLength - V2Prefix.Length;

    /// <summary>
    /// Splits a STUN USERNAME on the FIRST ':' only. An ICE ufrag never contains ':', so every later
    /// colon belongs to neither half and makes the username malformed; it is kept in the client half
    /// so that validation rejects it.
    /// </summary>
    public static bool TrySplitStunUsername(
        string? username,
        [NotNullWhen(true)] out string? serverUfrag,
        [NotNullWhen(true)] out string? clientUfrag)
    {
        serverUfrag = null;
        clientUfrag = null;

        int separator = username?.IndexOf(':') ?? -1;
        if (username is null || separator <= 0 || separator == username.Length - 1)
        {
            return false;
        }

        serverUfrag = username[..separator];
        clientUfrag = username[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// RFC 8839, section 5.4: ice-char is ALPHA / DIGIT / "+" / "/", and ufrags and passwords are a
    /// bounded run of them.
    /// </summary>
    public static bool IsIceString(string? value, int minLength, int maxLength)
    {
        if (value is null || value.Length < minLength || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool isIceChar = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '+' or '/';
            if (!isIceChar)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Selects the flow from the server ufrag's prefix. The spec reserves the <c>libp2p+webrtc+</c>
    /// namespace for versions: an unrecognized prefix, or no prefix at all, is
    /// <see cref="WebRtcDirectIceFlow.Unknown"/> and must be rejected rather than guessed at.
    /// </summary>
    public static WebRtcDirectIceFlow GetFlow(string? serverUfrag) => serverUfrag switch
    {
        not null when serverUfrag.StartsWith(V2Prefix, StringComparison.Ordinal) => WebRtcDirectIceFlow.V2,
        not null when serverUfrag.StartsWith(V1Prefix, StringComparison.Ordinal) => WebRtcDirectIceFlow.V1,
        _ => WebRtcDirectIceFlow.Unknown,
    };

    public static bool TryParseStunUsername(string? username, out WebRtcIceCredentials credentials)
    {
        credentials = default;

        if (!TrySplitStunUsername(username, out string? serverUfrag, out string? clientUfrag) ||
            !IsIceString(serverUfrag, MinUfragLength, MaxUfragLength) ||
            !IsIceString(clientUfrag, MinUfragLength, MaxUfragLength))
        {
            return false;
        }

        WebRtcDirectIceFlow flow = GetFlow(serverUfrag);
        if (flow == WebRtcDirectIceFlow.Unknown)
        {
            return false;
        }

        credentials = new WebRtcIceCredentials(serverUfrag, clientUfrag, flow);
        return true;
    }

    /// <summary>
    /// Recovers <c>client_pwd</c> by stripping the v2 prefix from the server ufrag, rejecting a result
    /// that is not a valid ICE password.
    /// </summary>
    public bool TryGetClientPassword([NotNullWhen(true)] out string? clientPassword)
    {
        clientPassword = null;

        if (Flow != WebRtcDirectIceFlow.V2 || !ServerUfrag.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = ServerUfrag[V2Prefix.Length..];
        if (!IsIceString(candidate, MinPasswordLength, MaxPasswordLength))
        {
            return false;
        }

        clientPassword = candidate;
        return true;
    }

    /// <summary>
    /// Builds the ufrag and password a dialer puts in its synthetic remote answer:
    /// <c>libp2p+webrtc+v2/&lt;client_pwd&gt;</c>. The prefix counts toward the ufrag length limit.
    /// </summary>
    public static string BuildV2Ufrag(string clientPassword)
    {
        if (!IsIceString(clientPassword, MinPasswordLength, MaxPasswordLength))
        {
            throw new ArgumentException(
                $"ICE password must be {MinPasswordLength} to {MaxPasswordLength} ice-chars.", nameof(clientPassword));
        }

        string ufrag = V2Prefix + clientPassword;
        if (!IsIceString(ufrag, MinUfragLength, MaxUfragLength))
        {
            throw new ArgumentException(
                $"ICE password is too long: '{V2Prefix}' plus the password exceeds {MaxUfragLength} ice-chars.", nameof(clientPassword));
        }

        return ufrag;
    }
}
