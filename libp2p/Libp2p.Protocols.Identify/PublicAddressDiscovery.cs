// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Libp2p.Protocols;

/// <summary>Finds candidate public IPs from recent Identify responses for one local peer.</summary>
public sealed class PublicAddressDiscovery
{
    private const int MaxReportsPerFamily = 32;
    private static readonly TimeSpan ReportLifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly List<Report> _ipv4Reports = [];
    private readonly List<Report> _ipv6Reports = [];

    internal PublicAddressDiscovery(TimeProvider? timeProvider)
    {
        _clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Returns a candidate IP for the requested family, or null when no source yields one.</summary>
    /// <remarks>
    /// At least two fresh reports and a strict majority of independent reporters are needed.
    /// When the reports yield no candidate, the optional lookup is invoked once with the supplied
    /// cancellation token. The lookup owns its network requests and can report failure by returning
    /// null. Lookup exceptions propagate to the caller. Results carry no port or reachability claim.
    /// </remarks>
    public async ValueTask<IPAddress?> GetCandidateAsync(AddressFamily addressFamily,
        Func<AddressFamily, CancellationToken, ValueTask<IPAddress?>>? externalLookup = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<Report> reports = addressFamily switch
        {
            AddressFamily.InterNetwork => _ipv4Reports,
            AddressFamily.InterNetworkV6 => _ipv6Reports,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
        };
        IPAddress? candidate = GetObservedCandidate(reports);
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate is not null || externalLookup is null) return candidate;

        IPAddress? result = await externalLookup(addressFamily, cancellationToken).AsTask()
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        candidate = GetPublicIP(result);
        return candidate?.AddressFamily == addressFamily ? candidate : null;
    }

    internal void Record(PeerId reporter, Multiaddress? remoteAddress, ReadOnlySpan<byte> observation)
    {
        if (remoteAddress is not { Protocols: [_, TCP or UDP, ..] } ||
            GetPublicIP(remoteAddress.GetDirectIPAddress()) is not { } reporterIP)
        {
            return;
        }

        IPAddress? observed = ReadObservation(observation, remoteAddress.Protocols[1] is TCP);
        if (observed is null || observed.AddressFamily != reporterIP.AddressFamily)
        {
            return;
        }

        byte[] networkBytes = reporterIP.GetAddressBytes();
        // Count an IPv6 /56 as one network, following go-libp2p's observer grouping.
        if (reporterIP.AddressFamily == AddressFamily.InterNetworkV6) networkBytes.AsSpan(7).Clear();
        IPAddress network = new(networkBytes);
        PeerId reporterId = new([.. reporter.Bytes]);
        List<Report> reports = observed.AddressFamily == AddressFamily.InterNetwork ? _ipv4Reports : _ipv6Reports;

        lock (_gate)
        {
            long timestamp = _clock.GetTimestamp();
            reports.RemoveAll(report => report.Reporter == reporterId || report.Network.Equals(network)
                || IsExpired(report, timestamp));
            if (reports.Count == MaxReportsPerFamily) reports.RemoveAt(0);
            reports.Add(new(reporterId, network, observed, timestamp));
        }
    }

    private IPAddress? GetObservedCandidate(List<Report> reports)
    {
        lock (_gate)
        {
            long timestamp = _clock.GetTimestamp();
            reports.RemoveAll(report => IsExpired(report, timestamp));
            if (reports.Count < 2) return null;

            int majority = reports.Count / 2 + 1;
            Dictionary<IPAddress, int> votes = [];
            foreach (Report report in reports)
            {
                int count = votes.GetValueOrDefault(report.Observed) + 1;
                if (count == majority) return new IPAddress(report.Observed.GetAddressBytes());
                votes[report.Observed] = count;
            }
            return null;
        }
    }

    private bool IsExpired(Report report, long timestamp) =>
        _clock.GetElapsedTime(report.Timestamp, timestamp) >= ReportLifetime;

    private static IPAddress? GetPublicIP(IPAddress? ip)
    {
        if (ip is null || !ip.IsPublicAddressCandidate()) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static IPAddress? ReadObservation(ReadOnlySpan<byte> address, bool reportedOverTcp)
    {
        // The general multiaddress decoder accepts variable-length components. Allow only the
        // fixed layouts needed for direct TCP and UDP endpoints before constructing an IP.
        if (address.Length is < 8 or > 24) return null;
        int ipLength = address[0] switch { 4 => 4, 41 => 16, _ => 0 };
        if (ipLength == 0 || address.Length < ipLength + 4) return null;

        ReadOnlySpan<byte> transport = address[(ipLength + 1)..];
        int portOffset = transport switch
        {
            [6 or 17, _, _, ..] => 1, // TCP or the legacy UDP code used by the multiaddress package
            [0x91, 0x02, _, _, ..] => 2, // UDP multicodec 273
            _ => 0
        };
        bool isTcp = transport[0] == 6;
        if (portOffset == 0 || isTcp != reportedOverTcp || (transport[portOffset] | transport[portOffset + 1]) == 0) return null;

        ReadOnlySpan<byte> suffix = transport[(portOffset + 2)..];
        bool direct = isTcp
            ? suffix is [] or [0xdd, 0x03] or [0xde, 0x03] or [0xc0, 0x03, 0xdd, 0x03] // ws, wss, tls/ws
            : suffix is [] or [0xcc, 0x03] or [0xcd, 0x03] or [0x98, 0x02]; // quic, quic-v1, webrtc-direct
        return direct ? GetPublicIP(new IPAddress(address.Slice(1, ipLength))) : null;
    }

    private readonly record struct Report(PeerId Reporter, IPAddress Network, IPAddress Observed, long Timestamp);
}

public static class PublicAddressDiscoveryExtensions
{
    /// <summary>Enables candidate public IP discovery for each peer created by this builder.</summary>
    public static TBuilder WithPublicAddressDiscovery<TBuilder>(this TBuilder builder) where TBuilder : IPeerFactoryBuilder
    {
        if (builder is not IPeerServiceBuilder services)
        {
            throw new NotSupportedException("Public address discovery requires a builder with peer-owned services.");
        }

        services.AddPeerService((_, provider) => new PublicAddressDiscovery(provider.GetService(typeof(TimeProvider)) as TimeProvider),
            ownsInstance: false);
        return builder;
    }

    /// <summary>Gets the peer's public IP discovery service when enabled.</summary>
    public static PublicAddressDiscovery? GetPublicAddressDiscovery(this ILocalPeer peer) =>
        (peer as IPeerServices)?.GetPeerService<PublicAddressDiscovery>();
}
