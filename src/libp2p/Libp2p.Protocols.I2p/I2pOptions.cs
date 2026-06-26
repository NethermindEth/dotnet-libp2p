// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;

namespace Nethermind.Libp2p.Protocols.I2p;

public sealed class I2pOptions
{
    public const int DefaultSamPort = 7656;
    public const int DefaultSamUdpPort = 7655;
    public const int DefaultMaxDatagramPayloadSize = 31_744;

    private bool? _usePrimarySessionForStreams;

    public string SamHost { get; set; } = IPAddress.Loopback.ToString();
    public int SamPort { get; set; } = DefaultSamPort;
    public string SamUdpHost { get; set; } = IPAddress.Loopback.ToString();
    public int SamUdpPort { get; set; } = DefaultSamUdpPort;
    public string SessionId { get; set; } = $"libp2p-{Guid.NewGuid():N}";
    public string StreamSessionId { get; set; } = $"libp2p-stream-{Guid.NewGuid():N}";
    public string DatagramSessionId { get; set; } = $"libp2p-udp-{Guid.NewGuid():N}";
    public string PrimarySessionStyle { get; set; } = "MASTER";
    public bool UsePrimarySessionForStreams
    {
        get => _usePrimarySessionForStreams ?? true;
        set => _usePrimarySessionForStreams = value;
    }

    internal bool HasExplicitUsePrimarySessionForStreams => _usePrimarySessionForStreams.HasValue;
    public string Destination { get; set; } = "TRANSIENT";
    public string? DestinationKeyFile { get; set; }
    public string DestinationSignatureType { get; set; } = "7";
    public int ConnectTimeoutMilliseconds { get; set; } = 15_000;
    public string? DatagramHost { get; set; }
    public int DatagramPort { get; set; }
    public int MaxDatagramPayloadSize { get; set; } = DefaultMaxDatagramPayloadSize;
    public Dictionary<string, string> SessionOptions { get; } = new(StringComparer.Ordinal)
    {
        ["i2cp.leaseSetEncType"] = "4,0"
    };
}
