// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Libp2p.Protocols.KadDht;

public class KadDhtOptions
{
    /// <summary>
    /// The K parameter for Kademlia - size of each K-bucket and number of nodes to return in lookups.
    /// </summary>
    public int KSize { get; set; } = 20;

    /// <summary>
    /// The Alpha parameter for Kademlia - degree of parallelism for network operations.
    /// </summary>
    public int Alpha { get; set; } = 3;

    /// <summary>
    /// Operating mode for the DHT node.
    /// </summary>
    public KadDhtMode Mode { get; set; } = KadDhtMode.Server;

    /// <summary>
    /// How often to refresh buckets in the routing table.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Time-to-live for stored records (values and provider records).
    /// </summary>
    public TimeSpan RecordTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum number of provider records to store per key.
    /// </summary>
    public int MaxProvidersPerKey { get; set; } = 20;

    /// <summary>
    /// Maximum size of a value that can be stored (in bytes).
    /// </summary>
    public int MaxValueSize { get; set; } = 65536; // 64KB

    /// <summary>
    /// Maximum number of values to store locally.
    /// </summary>
    public int MaxStoredValues { get; set; } = 1000;

    /// <summary>
    /// Timeout for individual network operations.
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Operating mode for Kad-DHT nodes.
/// </summary>
public enum KadDhtMode
{
    /// <summary>
    /// Client mode - participates in routing but does not store records.
    /// Suitable for lightweight clients or nodes with limited storage.
    /// </summary>
    Client,

    /// <summary>
    /// Server mode - full DHT participant that stores records and responds to queries.
    /// Suitable for stable nodes with adequate storage capacity.
    /// </summary>
    Server
}