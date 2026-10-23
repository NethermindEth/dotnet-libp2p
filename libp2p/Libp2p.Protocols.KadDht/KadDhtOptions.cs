using System;
using Libp2p.Protocols.KadDht.Storage;

namespace Libp2p.Protocols.KadDht;

/// <summary>
/// Configuration options for Kademlia DHT
/// </summary>
public class KadDhtOptions
{
    public const string DefaultProtocolId = "/ipfs/kad/1.0.0";

    /// <summary>
    /// Libp2p protocol ID used by the Kad-DHT request/response protocol.
    /// Default: /ipfs/kad/1.0.0
    /// </summary>
    public string ProtocolId { get; set; } = DefaultProtocolId;

    /// <summary>
    /// Number of nodes to return in each query (K parameter)
    /// Default: 20
    /// </summary>
    public int KSize { get; set; } = 20;

    /// <summary>Maximum encoded peer identity bytes. Default: 128.</summary>
    public int MaxPeerIdBytes { get; set; } = 128;

    /// <summary>Closer-peer address inspection slots per peer. Default: 16.</summary>
    public int MaxAddressesPerPeer { get; set; } = 16;

    /// <summary>Per-response peer limit for encoded and retained payload bytes, independently. Default: 8 KiB.</summary>
    public int MaxPeerRecordBytes { get; set; } = 8 * 1024;

    /// <summary>Combined closer/provider response budget for encoded and retained bytes, independently. Default: 64 KiB.</summary>
    public int MaxPeerResponseBytes { get; set; } = 64 * 1024;

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
    /// Maximum number of keys retained by the default provider store. New keys are
    /// rejected at capacity unless a fully expired key can be reclaimed. Default: 10,000.
    /// </summary>
    public int MaxProviderKeys { get; set; } = ProviderRecordLimits.DefaultMaxProviderKeys;

    /// <summary>
    /// Maximum number of address entries inspected per provider record. Default: 16.
    /// Skipped or malformed entries still consume a slot.
    /// </summary>
    public int MaxAddressesPerProvider { get; set; } = ProviderRecordLimits.DefaultMaxAddressesPerProvider;

    /// <summary>
    /// Maximum retained variable-length payload bytes per provider record. Includes
    /// the Base64 key and Base58 peer dictionary key (UTF-16), peer ID bytes, and
    /// address strings (UTF-16). Oversized identities are rejected; whole addresses
    /// that do not fit are omitted. Default: 1,024.
    /// </summary>
    public int MaxProviderRecordBytes { get; set; } = ProviderRecordLimits.DefaultMaxProviderRecordBytes;

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
    /// Number of disjoint lookup paths to use for Sybil resistance.
    /// Set to 0 to disable disjoint path queries (uses standard single-path lookup).
    /// </summary>
    public int DisjointPaths { get; set; } = 0;

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
