// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

/// <summary>Immutable limits for a local peer. Counts include work still stopping after cancellation.</summary>
public sealed class PeerResourceLimits
{
    public static PeerResourceLimits Default { get; } = new();

    public PeerResourceLimits(int maxInboundTcpConnections = 256, int maxInboundTcpConnectionsPerIp = 32,
        int maxConcurrentInitializations = 128, TimeSpan? inboundUpgradeTimeout = null,
        TimeSpan? initializationTimeout = null, TimeSpan? negotiationTimeout = null, TimeSpan? inboundTcpIdleTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundTcpConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInboundTcpConnectionsPerIp);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentInitializations);
        MaxInboundTcpConnections = maxInboundTcpConnections;
        MaxInboundTcpConnectionsPerIp = maxInboundTcpConnectionsPerIp;
        MaxConcurrentInitializations = maxConcurrentInitializations;
        InboundUpgradeTimeout = Validate(inboundUpgradeTimeout ?? TimeSpan.FromSeconds(15));
        InitializationTimeout = Validate(initializationTimeout ?? TimeSpan.FromSeconds(15));
        NegotiationTimeout = Validate(negotiationTimeout ?? TimeSpan.FromSeconds(15));
        InboundTcpIdleTimeout = Validate(inboundTcpIdleTimeout ?? TimeSpan.FromSeconds(60));
    }

    public PeerResourceLimits(int maxInboundTcpConnections, int maxInboundTcpConnectionsPerIp, int maxConcurrentInitializations,
        TimeSpan? inboundUpgradeTimeout, TimeSpan? initializationTimeout, TimeSpan? negotiationTimeout)
        : this(maxInboundTcpConnections, maxInboundTcpConnectionsPerIp, maxConcurrentInitializations,
            inboundUpgradeTimeout, initializationTimeout, negotiationTimeout, null)
    { }

    private static TimeSpan Validate(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return timeout;
    }

    public int MaxInboundTcpConnections { get; }
    public int MaxInboundTcpConnectionsPerIp { get; }
    public int MaxConcurrentInitializations { get; }
    public TimeSpan InboundUpgradeTimeout { get; }
    public TimeSpan InitializationTimeout { get; }
    public TimeSpan NegotiationTimeout { get; }
    /// <summary>Minimum time without application operations before an established TCP connection may be evicted under admission pressure.</summary>
    public TimeSpan InboundTcpIdleTimeout { get; }
}
