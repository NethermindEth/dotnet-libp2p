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
    /// Number of concurrent queries to run
    /// Default: 3
    /// </summary>
    public int Alpha { get; set; } = 3;

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
    /// Default: 24 hours
    /// </summary>
    public TimeSpan ProviderRecordTtl { get; set; } = TimeSpan.FromHours(24);

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
