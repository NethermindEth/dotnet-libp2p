// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

/// <summary>
/// Synthesises the server's SDP answer locally from a /webrtc-direct multiaddr, so that no
/// signalling channel is needed. Mirrors the ICE Lite answer templates of go-libp2p
/// (p2p/transport/webrtc/sdp.go, serverSDP) and js-libp2p (serverAnswerFromMultiaddr), both of
/// which use LF line endings.
/// </summary>
internal static class WebRtcDirectAnswerSdp
{
    public static string Build(IPEndPoint serverEndpoint, DtlsFingerprint serverFingerprint, string clientPwd)
    {
        ArgumentNullException.ThrowIfNull(serverEndpoint);
        ArgumentNullException.ThrowIfNull(serverFingerprint);

        // WebRtcIceCredentials owns the v2 ufrag rule, including the length bound that the
        // prefix counts against; a second copy here drifted from it once already.
        string credential = WebRtcIceCredentials.BuildV2Ufrag(clientPwd);

        string ipFamily = serverEndpoint.AddressFamily == AddressFamily.InterNetwork ? "IP4" : "IP6";
        string host = FormatAddress(serverEndpoint.Address);
        int port = serverEndpoint.Port;

        return string.Join('\n', [
            "v=0",
            $"o=- 0 0 IN {ipFamily} {host}",
            "s=-",
            "t=0 0",
            "a=ice-lite",
            $"m=application {port} UDP/DTLS/SCTP webrtc-datachannel",
            $"c=IN {ipFamily} {host}",
            "a=mid:0",
            "a=ice-options:ice2",
            $"a=ice-ufrag:{credential}",
            $"a=ice-pwd:{credential}",
            $"a=fingerprint:{serverFingerprint.ToSdpString()}",
            "a=setup:passive",
            "a=sctp-port:5000",
            "a=max-message-size:16384",
            $"a=candidate:1467250027 1 UDP 1467250027 {host} {port} typ host",
            "a=end-of-candidates",
            string.Empty
        ]);
    }

    /// <summary>
    /// A scoped IPv6 address renders as "fe80::1%eth0", which is not an RFC 4566 unicast-address
    /// and is meaningless to the remote peer in any case.
    /// </summary>
    private static string FormatAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes()).ToString()
            : address.ToString();
}
