using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core.Discovery;
using Microsoft.Extensions.Options;
using Nethermind.Libp2p.Core;
using System.Linq;

namespace Libp2p.Protocols.KadDht.Extensions
{
    /// <summary>
    /// Extension methods for IHostBuilder to add KadDht.
    /// </summary>
    public static class HostBuilderExtensions
    {
        /// <summary>
        /// Adds the Kademlia DHT protocol to the host.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="configureOptions">Optional action to configure the DHT options.</param>
        /// <returns>The host builder.</returns>
        public static IHostBuilder AddKadDht(this IHostBuilder builder, Action<KadDhtOptions>? configureOptions = null)
        {
            return builder.ConfigureServices((context, services) =>
            {
                // Add options
                if (configureOptions != null)
                {
                    services.Configure(configureOptions);
                }
                else
                {
                    services.Configure<KadDhtOptions>(options => { });
                }

                // Add host adapter
                services.AddSingleton<IHost, HostAdapter>(sp =>
                {
                    var localPeer = sp.GetRequiredService<ILocalPeer>();
                    return new HostAdapter(localPeer);
                });

                // Add protocol
                services.AddSingleton<KadDhtProtocol>();
                services.AddSingleton<ISessionProtocol>(sp => sp.GetRequiredService<KadDhtProtocol>());
                services.AddSingleton<IContentRouter>(sp => sp.GetRequiredService<KadDhtProtocol>());

                // Add key operators - these are essential for peer discovery
                services.AddSingleton<PeerIdKeyOperator>();
                services.AddSingleton<IKeyOperator<PeerId, ValueHash256>>(sp => sp.GetRequiredService<PeerIdKeyOperator>());
                services.AddSingleton<ValueHash256KeyOperator>();
                
                // Add log manager adapter
                services.AddSingleton<ILogManager>(sp =>
                    new Libp2p.Protocols.KadDht.InternalTable.Logging.LogManagerAdapter(sp.GetRequiredService<ILoggerFactory>()));

                // Add Kademlia core components - minimum set for peer discovery
                services.AddSingleton<KBucketTree<ValueHash256>>();
                services.AddSingleton<IRoutingTable<ValueHash256>, ValueHash256RoutingTable>();
                services.AddSingleton<INodeHashProvider<ValueHash256>, ValueHash256NodeHashProvider>();
                services.AddSingleton<INodeHealthTracker<ValueHash256>, NodeHealthTracker<ValueHash256, ValueHash256>>();
                
                // Configure ValueHash256KademliaMessageSender
                services.AddSingleton<ValueHash256KademliaMessageSender>();
                services.AddSingleton<IKademliaMessageSender<ValueHash256, ValueHash256>>(sp => 
                {
                    var kadDhtProtocol = sp.GetRequiredService<KadDhtProtocol>();
                    var logger = sp.GetRequiredService<ILogger<ValueHash256KademliaMessageSender>>();
                    var peerIdKeyOperator = sp.GetRequiredService<PeerIdKeyOperator>();
                    return new ValueHash256KademliaMessageSender(kadDhtProtocol, logger, peerIdKeyOperator);
                });
                
                // Add lookup algorithm
                services.AddSingleton<ILookupAlgo<ValueHash256>>(sp =>
                {
                    var routingTable = sp.GetRequiredService<IRoutingTable<ValueHash256>>();
                    var nodeHashProvider = sp.GetRequiredService<INodeHashProvider<ValueHash256>>();
                    var nodeHealthTracker = sp.GetRequiredService<INodeHealthTracker<ValueHash256>>();
                    var config = sp.GetRequiredService<KademliaConfig<ValueHash256>>();
                    var logManager = sp.GetRequiredService<ILogManager>();
                    return new LookupKNearestNeighbour<ValueHash256, ValueHash256>(
                        routingTable,
                        nodeHashProvider,
                        nodeHealthTracker,
                        config,
                        logManager);
                });
                
                // Configure KademliaConfig
                services.AddSingleton<KademliaConfig<ValueHash256>>(sp =>
                {
                    var options = sp.GetRequiredService<IOptions<KadDhtOptions>>().Value;
                    var peerIdKeyOperator = sp.GetRequiredService<IKeyOperator<PeerId, ValueHash256>>();
                    var valueHash256KeyOperator = sp.GetRequiredService<ValueHash256KeyOperator>();
                    var host = sp.GetRequiredService<IHost>();
                    
                    // Create the config with the host's PeerId
                    var hostPeerId = host.GetPeerId();
                    var nodeId = peerIdKeyOperator.GetKey(hostPeerId);
                    
                    return new KademliaConfig<ValueHash256>
                    {
                        CurrentNodeId = nodeId,
                        KSize = options.BucketSize,
                        Alpha = options.Alpha,
                        Beta = options.Beta,
                        RefreshInterval = options.RefreshInterval,
                        BootNodes = options.BootstrapPeers
                            .Select(peerId => peerIdKeyOperator.GetKey(peerId))
                            .ToList()
                    };
                });
                
                // Add Kademlia main service
                services.AddSingleton<IKademlia<ValueHash256, ValueHash256>>(sp =>
                {
                    var valueHash256KeyOperator = sp.GetRequiredService<ValueHash256KeyOperator>();
                    var sender = sp.GetRequiredService<IKademliaMessageSender<ValueHash256, ValueHash256>>();
                    var routingTable = sp.GetRequiredService<IRoutingTable<ValueHash256>>();
                    var lookupAlgo = sp.GetRequiredService<ILookupAlgo<ValueHash256>>();
                    var logger = sp.GetRequiredService<ILogger<Kademlia<ValueHash256, ValueHash256>>>();
                    var nodeHealthTracker = sp.GetRequiredService<INodeHealthTracker<ValueHash256>>();
                    var config = sp.GetRequiredService<KademliaConfig<ValueHash256>>();

                    return new Kademlia<ValueHash256, ValueHash256>(
                        valueHash256KeyOperator,
                        sender,
                        routingTable,
                        lookupAlgo,
                        logger,
                        nodeHealthTracker,
                        config);
                });
                
                // Add PeerId-based Kademlia interface
                services.AddSingleton<IKademlia<PeerId, ValueHash256>>(sp =>
                {
                    var valueHash256Kademlia = sp.GetRequiredService<IKademlia<ValueHash256, ValueHash256>>();
                    var peerIdKeyOperator = sp.GetRequiredService<PeerIdKeyOperator>();
                    var logger = sp.GetRequiredService<ILogger<PeerId>>();
                    
                    // Return an adapter that wraps the ValueHash256 Kademlia
                    return new PeerIdKademliaAdapter(valueHash256Kademlia, peerIdKeyOperator, logger);
                });
            });
        }
        
        /// <summary>
        /// Adapter class that exposes the ValueHash256 Kademlia as a PeerId Kademlia.
        /// </summary>
        private class PeerIdKademliaAdapter : IKademlia<PeerId, ValueHash256>
        {
            private readonly IKademlia<ValueHash256, ValueHash256> _innerKademlia;
            private readonly PeerIdKeyOperator _keyOperator;
            private readonly ILogger _logger;
            
            public PeerIdKademliaAdapter(
                IKademlia<ValueHash256, ValueHash256> innerKademlia,
                PeerIdKeyOperator keyOperator,
                ILogger logger)
            {
                _innerKademlia = innerKademlia;
                _keyOperator = keyOperator;
                _logger = logger;
            }

            public void AddOrRefresh(ValueHash256 node)
            {
                _innerKademlia.AddOrRefresh(node);
            }

            public ValueHash256[] GetKNeighbour(PeerId target, ValueHash256? excluding = null, bool excludeSelf = false)
            {
                var targetHash = _keyOperator.GetKey(target);
                return _innerKademlia.GetKNeighbour(targetHash, excluding, excludeSelf);
            }

            public Task Bootstrap(CancellationToken token)
            {
                return _innerKademlia.Bootstrap(token);
            }

            public Task<ValueHash256[]> LookupNodesClosest(PeerId key, CancellationToken token, int? k = null)
            {
                var keyHash = _keyOperator.GetKey(key);
                return _innerKademlia.LookupNodesClosest(keyHash, token, k);
            }

            public void Remove(ValueHash256 node)
            {
                _innerKademlia.Remove(node);
            }

            public Task Run(CancellationToken token)
            {
                return _innerKademlia.Run(token);
            }
            
            public event EventHandler<ValueHash256>? OnNodeAdded
            {
                add => _innerKademlia.OnNodeAdded += value;
                remove => _innerKademlia.OnNodeAdded -= value;
            }

            public IEnumerable<ValueHash256> IterateNodes()
            {
                return _innerKademlia.IterateNodes();
            }
        }
    }
}