// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Base;
using Multiformats.Hash;
using Nethermind.Libp2p.Core;
using System.Net;

namespace Nethermind.Libp2p.Protocols.WebRtc.Internals;

internal static class WebRtcDirectMultiaddr
{
    public static bool IsWebRtcDirect(Multiaddress addr)
    {
        MultiaddrProtocolRegistry.EnsureRegistered();
        ArgumentNullException.ThrowIfNull(addr);
        return addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("webrtc-direct", StringComparer.OrdinalIgnoreCase);
    }

    public static (IPEndPoint Endpoint, DtlsFingerprint ExpectedFingerprint) Parse(Multiaddress addr)
    {
        MultiaddrProtocolRegistry.EnsureRegistered();
        ArgumentNullException.ThrowIfNull(addr);

        string[] segments = addr.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 7)
        {
            throw new FormatException($"Malformed webrtc-direct multiaddr: {addr}");
        }

        string? ipValue = GetValue(segments, "ip4") ?? GetValue(segments, "ip6");
        string? portValue = GetValue(segments, "udp");
        bool hasWebRtcDirect = segments.Contains("webrtc-direct", StringComparer.OrdinalIgnoreCase);
        string? certHashValue = GetValue(segments, "certhash");

        if (!hasWebRtcDirect || ipValue is null || portValue is null || certHashValue is null)
        {
            throw new FormatException($"Expected /ip4|ip6/.../udp/.../webrtc-direct/certhash/... in multiaddr: {addr}");
        }

        if (!IPAddress.TryParse(ipValue, out IPAddress? ipAddress))
        {
            throw new FormatException($"Invalid IP component in multiaddr: {ipValue}");
        }

        if (!int.TryParse(portValue, out int udpPort) || udpPort < IPEndPoint.MinPort || udpPort > IPEndPoint.MaxPort)
        {
            throw new FormatException($"Invalid UDP port component in multiaddr: {portValue}");
        }

        byte[] multihashBytes;
        try
        {
            multihashBytes = Multibase.Decode(certHashValue, out MultibaseEncoding _);
        }
        catch (Exception ex)
        {
            throw new FormatException("Invalid /certhash component in multiaddr.", ex);
        }

        DtlsFingerprint fingerprint;
        try
        {
            fingerprint = DtlsFingerprint.ParseFromMultihash(multihashBytes);
        }
        catch (Exception ex)
        {
            throw new FormatException("Malformed /certhash multihash payload.", ex);
        }

        return (new IPEndPoint(ipAddress, udpPort), fingerprint);
    }

    public static Multiaddress Build(IPEndPoint endpoint, DtlsFingerprint fingerprint)
    {
        MultiaddrProtocolRegistry.EnsureRegistered();
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(fingerprint);

        string ipProto = endpoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "ip4" : "ip6";
        string certHashText = Multibase.Encode(MultibaseEncoding.Base64Url, fingerprint.ToMultihashBytes());
        return Multiaddress.Decode($"/{ipProto}/{endpoint.Address}/udp/{endpoint.Port}/webrtc-direct/certhash/{certHashText}");
    }

    private static string? GetValue(string[] segments, string componentName)
    {
        int index = Array.FindIndex(segments, p => p.Equals(componentName, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < segments.Length ? segments[index + 1] : null;
    }
}