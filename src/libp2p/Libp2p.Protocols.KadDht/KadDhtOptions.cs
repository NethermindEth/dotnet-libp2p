using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Libp2p.Protocols.KadDht.InternalTable.Logging;
using Libp2p.Protocols.KadDht.InternalTable.Caching;
using Libp2p.Protocols.KadDht.InternalTable.Threading;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht
{
    /// <summary>
    /// Configuration options for the Kademlia DHT protocol.
    /// </summary>
    public class KadDhtOptions
    {
        /// <summary>
        /// Gets or sets the protocol ID.
        /// </summary>
        public string ProtocolId { get; set; } = "/ipfs/kad/1.0.0";

        /// <summary>
        /// Gets or sets the bucket size (k value in Kademlia).
        /// </summary>
        public int BucketSize { get; set; } = 20;

        /// <summary>
        /// Gets or sets the alpha value (concurrency parameter).
        /// </summary>
        public int Alpha { get; set; } = 3;

        /// <summary>
        /// Gets or sets the maximum number of records to store.
        /// </summary>
        public int MaxRecords { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the maximum number of providers to store.
        /// </summary>
        public int MaxProviders { get; set; } = 1000;

        /// <summary>
        /// Gets or sets the record expiry time.
        /// </summary>
        public TimeSpan RecordExpiry { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Gets or sets the provider record expiry time.
        /// </summary>
        public TimeSpan ProviderExpiry { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Gets or sets the interval for refreshing buckets.
        /// </summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Gets or sets the interval for republishing records.
        /// </summary>
        public TimeSpan RepublishInterval { get; set; } = TimeSpan.FromHours(23);

        /// <summary>
        /// Gets or sets the interval for cleaning up expired records.
        /// </summary>
        public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Gets or sets whether to enable server mode.
        /// </summary>
        public bool EnableServerMode { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable client mode.
        /// </summary>
        public bool EnableClientMode { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable value storage.
        /// </summary>
        public bool EnableValueStorage { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to enable provider storage.
        /// </summary>
        public bool EnableProviderStorage { get; set; } = true;

        /// <summary>
        /// Gets or sets the maximum size of a value that can be stored.
        /// </summary>
        public int MaxValueSize { get; set; } = 1024 * 1024; // 1 MB

        /// <summary>
        /// The beta parameter in Kademlia, used for accelerated lookups.
        /// </summary>
        public int Beta { get; set; } = 2;
        
        /// <summary>
        /// The timeout for find neighbor operations during lookups.
        /// </summary>
        public TimeSpan LookupTimeout { get; set; } = TimeSpan.FromSeconds(10);
        
        /// <summary>
        /// The timeout for ping operations during refreshes.
        /// </summary>
        public TimeSpan PingTimeout { get; set; } = TimeSpan.FromSeconds(1);
        
        /// <summary>
        /// The number of failed requests to a node before removing it from the routing table.
        /// </summary>
        public int NodeFailureThreshold { get; set; } = 5;
        
        /// <summary>
        /// The list of bootstrap peers to use when initializing the DHT.
        /// </summary>
        public ICollection<PeerId> BootstrapPeers { get; set; } = new List<PeerId>();
        
        /// <summary>
        /// The mode in which the DHT should operate.
        /// </summary>
        public KadDhtMode Mode { get; set; } = KadDhtMode.Server;
    }
} 
