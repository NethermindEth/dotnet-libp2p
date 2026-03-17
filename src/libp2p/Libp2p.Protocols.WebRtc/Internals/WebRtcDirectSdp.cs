// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using SIPSorcery.Net;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal static class WebRtcDirectSdp
{
    public static DtlsFingerprint ExtractFingerprint(string sdp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sdp);

        string? fingerprintLine = sdp
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase));

        if (fingerprintLine is null)
        {
            throw new FormatException("SDP does not contain an a=fingerprint line.");
        }

        string rawFingerprint = fingerprintLine["a=fingerprint:".Length..].Trim();
        return DtlsFingerprint.ParseFromSdp(rawFingerprint);
    }

    public static RTCSessionDescriptionInit BuildOffer(
        IPEndPoint serverEndpoint,
        DtlsFingerprint localFingerprint)
    {
        string ipFamily = serverEndpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "IP4" : "IP6";
        string ufrag = RandomToken(8);
        string pwd = RandomToken(24);

        string sdp = string.Join("\r\n", new[]
        {
            "v=0",
            $"o=- 0 0 IN {ipFamily} {serverEndpoint.Address}",
            "s=-",
            "t=0 0",
            "a=group:BUNDLE 0",
            "a=ice-options:trickle",
            $"m=application {serverEndpoint.Port} UDP/DTLS/SCTP webrtc-datachannel",
            $"c=IN {ipFamily} {serverEndpoint.Address}",
            "a=mid:0",
            "a=setup:active",
            $"a=ice-ufrag:{ufrag}",
            $"a=ice-pwd:{pwd}",
            $"a=fingerprint:{localFingerprint.ToSdpString()}",
            "a=sctp-port:5000",
            "a=max-message-size:16384",
            $"a=candidate:1 1 udp 2130706431 {serverEndpoint.Address} {serverEndpoint.Port} typ host",
            "a=end-of-candidates",
            string.Empty
        });

        return new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdp,
        };
    }

    public static RTCSessionDescriptionInit BuildAnswer(
        RTCSessionDescriptionInit remoteOffer,
        DtlsFingerprint localFingerprint)
    {
        string offerSdp = remoteOffer.sdp ?? string.Empty;
        Match candidate = Regex.Match(offerSdp, @"a=candidate:[^\r\n]*\s(?<ip>[^\s]+)\s(?<port>\d+)\s+typ\s+host", RegexOptions.IgnoreCase);

        string ip = candidate.Success ? candidate.Groups["ip"].Value : "0.0.0.0";
        string port = candidate.Success ? candidate.Groups["port"].Value : "9";
        string ipFamily = IPAddress.TryParse(ip, out IPAddress? parsed) && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "IP6"
            : "IP4";

        string sdp = string.Join("\r\n", new[]
        {
            "v=0",
            $"o=- 0 0 IN {ipFamily} {ip}",
            "s=-",
            "t=0 0",
            "a=group:BUNDLE 0",
            "a=ice-options:trickle",
            $"m=application {port} UDP/DTLS/SCTP webrtc-datachannel",
            $"c=IN {ipFamily} {ip}",
            "a=mid:0",
            "a=setup:passive",
            "a=ice-ufrag:listener",
            "a=ice-pwd:listenerpassword",
            $"a=fingerprint:{localFingerprint.ToSdpString()}",
            "a=sctp-port:5000",
            "a=max-message-size:16384",
            string.Empty
        });

        return new RTCSessionDescriptionInit
        {
            type = RTCSdpType.answer,
            sdp = sdp,
        };
    }

    private static string RandomToken(int bytesLength)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(bytesLength);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}