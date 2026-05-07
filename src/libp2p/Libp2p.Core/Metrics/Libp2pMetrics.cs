// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;

namespace Nethermind.Libp2p.Core.Metrics;

/// <summary>
/// Centralized metrics for dotnet-libp2p using System.Diagnostics.Metrics.
/// Tracks connection establishment, session lifecycle, data transfer, and protocol negotiation.
/// </summary>
public static class Libp2pMetrics
{
    public const string MeterName = "dotnet-libp2p";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    // --- Connection metrics ---
    public static readonly Counter<long> ConnectionsOpened =
        Meter.CreateCounter<long>("libp2p.connections.opened", description: "Total connections opened");

    public static readonly UpDownCounter<long> ConnectionsActive =
        Meter.CreateUpDownCounter<long>("libp2p.connections.active", description: "Currently active connections");

    // --- Session metrics ---
    public static readonly Counter<long> SessionsOpened =
        Meter.CreateCounter<long>("libp2p.sessions.opened", description: "Total sessions opened");

    public static readonly Counter<long> SessionsClosed =
        Meter.CreateCounter<long>("libp2p.sessions.closed", description: "Total sessions closed");

    public static readonly UpDownCounter<long> SessionsActive =
        Meter.CreateUpDownCounter<long>("libp2p.sessions.active", description: "Currently active sessions");

    // --- Dial metrics ---
    public static readonly Histogram<double> DialDuration =
        Meter.CreateHistogram<double>("libp2p.dial.duration", unit: "ms", description: "Time to establish a connection via dial");

    public static readonly Counter<long> DialAttempts =
        Meter.CreateCounter<long>("libp2p.dial.attempts", description: "Total dial attempts");

    public static readonly Counter<long> DialFailures =
        Meter.CreateCounter<long>("libp2p.dial.failures", description: "Total failed dial attempts");

    // --- Data transfer metrics ---
    public static readonly Counter<long> DataSentBytes =
        Meter.CreateCounter<long>("libp2p.data.sent_bytes", unit: "bytes", description: "Total bytes sent");

    public static readonly Counter<long> DataReceivedBytes =
        Meter.CreateCounter<long>("libp2p.data.received_bytes", unit: "bytes", description: "Total bytes received");

    public static readonly Counter<long> DataSentPackets =
        Meter.CreateCounter<long>("libp2p.data.sent_packets", description: "Total packets sent");

    public static readonly Counter<long> DataReceivedPackets =
        Meter.CreateCounter<long>("libp2p.data.received_packets", description: "Total packets received");

    // --- Protocol negotiation metrics ---
    public static readonly Counter<long> ProtocolNegotiations =
        Meter.CreateCounter<long>("libp2p.protocol.negotiations", description: "Total protocol negotiations");

    public static readonly Counter<long> ProtocolNegotiationFailures =
        Meter.CreateCounter<long>("libp2p.protocol.negotiation_failures", description: "Total failed protocol negotiations");

    // --- Error metrics ---
    public static readonly Counter<long> Errors =
        Meter.CreateCounter<long>("libp2p.errors", description: "Total errors encountered");
}
