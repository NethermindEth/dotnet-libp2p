using System;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Configuration options for Kademlia DHT
/// </summary>
public class KadDhtOptions
{
    /// <summary>
    /// Number of nodes to return in each query (K parameter)
    /// Default: 20
    /// </summary>
    public int KSize { get; set; } = 20;

    /// <summary>
    /// Number of concurrent queries to run (α concurrency parameter)
    /// Default: 10 per libp2p Kademlia DHT spec
    /// </summary>
    public int Alpha { get; set; } = 10;

    /// <summary>
    /// Operating mode for the DHT
    /// Default: Server
    /// </summary>
    public KadDhtMode Mode { get; set; } = KadDhtMode.Server;

    /// <summary>
    /// TTL for stored records
    /// Default: 24 hours
    /// </summary>
    public TimeSpan RecordTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum number of stored values
    /// Default: 1000
    /// </summary>
    public int MaxStoredValues { get; set; } = 1000;

    /// <summary>
    /// Maximum size of a value in bytes
    /// Default: 64KB
    /// </summary>
    public int MaxValueSize { get; set; } = 65536;

    /// <summary>
    /// Maximum number of providers per key
    /// Default: 20
    /// </summary>
    public int MaxProvidersPerKey { get; set; } = 20;

    /// <summary>
    /// Interval for refreshing routing table buckets
    /// Default: 1 hour
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Maximum message size in bytes
    /// Default: 1MB
    /// </summary>
    public int MaxMessageSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// TTL for provider records
    /// Default: 48 hours per libp2p spec (Provider Record Expiration)
    /// </summary>
    public TimeSpan ProviderRecordTtl { get; set; } = TimeSpan.FromHours(48);

    /// <summary>
    /// Interval at which locally-originated provider records are republished to the network.
    /// Default: 22 hours per libp2p spec (IPFS default)
    /// </summary>
    public TimeSpan ProviderRepublishInterval { get; set; } = TimeSpan.FromHours(22);

    /// <summary>
    /// Interval at which locally-originated values are republished to the network.
    /// Default: 22 hours (matches provider republish interval)
    /// </summary>
    public TimeSpan ValueRepublishInterval { get; set; } = TimeSpan.FromHours(22);

    /// <summary>
    /// Interval for running maintenance tasks (cleanup of expired records).
    /// Default: 1 hour
    /// </summary>
    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Timeout for DHT operations
    /// Default: 30 seconds
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// K parameter (same as KSize)
    /// Default: 20
    /// </summary>
    public int K => KSize;
}

/// <summary>
/// Kademlia DHT operating modes
/// </summary>
public enum KadDhtMode
{
    /// <summary>
    /// Client mode - participates in routing but doesn't store records
    /// </summary>
    Client,

    /// <summary>
    /// Server mode - participates fully in the DHT network
    /// </summary>
    Server
}
