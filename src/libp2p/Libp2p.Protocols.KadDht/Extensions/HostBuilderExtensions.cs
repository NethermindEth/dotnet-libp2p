using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Libp2p.Protocols.KadDht.Extensions
{
    public static class HostBuilderExtensions
    {
        public static IServiceCollection AddKadDht(this IServiceCollection services)
        {
            return services.AddKadDht(_ => { });
        }

        public static IServiceCollection AddKadDht(this IServiceCollection services, Action<KadDhtOptions> configure)
        {
            return services.Configure(configure)
                .AddSingleton<KadDhtProtocol>()
                .AddSingleton<IKademlia<PeerId, ValueHash256>, PeerIdKademliaAdapter>()
                .AddSingleton<IKademliaMessageSender<ValueHash256, ValueHash256>>(sp =>
                {
                    var kadDhtProtocol = sp.GetRequiredService<KadDhtProtocol>();
                    var logger = sp.GetRequiredService<ILogger<ValueHash256KademliaMessageSender>>();
                    var peerIdKeyOperator = sp.GetRequiredService<PeerIdKeyOperator>();
                    var sender = new ValueHash256KademliaMessageSender(kadDhtProtocol, logger, peerIdKeyOperator);
                    return sender;
                })
                .AddSingleton<IKademlia<ValueHash256, ValueHash256>>(sp =>
                {
                    var routingTable = sp.GetRequiredService<IRoutingTable<ValueHash256, ValueHash256>>();
                    var lookupAlgo = sp.GetRequiredService<ILookupAlgo<ValueHash256, ValueHash256>>();
                    var logger = sp.GetRequiredService<ILogger<Kademlia<ValueHash256, ValueHash256>>>();
                    var nodeHealthTracker = sp.GetRequiredService<INodeHealthTracker<ValueHash256>>();
                    var config = sp.GetRequiredService<KademliaConfig<ValueHash256>>();
                    var messageSender = sp.GetRequiredService<IKademliaMessageSender<ValueHash256, ValueHash256>>();
                    var keyOperator = sp.GetRequiredService<IKeyOperator<ValueHash256, ValueHash256>>();

                    return new Kademlia<ValueHash256, ValueHash256>(
                        keyOperator,
                        messageSender,
                        routingTable,
                        lookupAlgo,
                        logger,
                        nodeHealthTracker,
                        config);
                })
                .AddSingleton<IKademlia<PeerId, ValueHash256>>(sp =>
                {
                    var valueHash256Kademlia = sp.GetRequiredService<IKademlia<ValueHash256, ValueHash256>>();
                    var peerIdKeyOperator = sp.GetRequiredService<PeerIdKeyOperator>();
                    var logger = sp.GetRequiredService<ILogger<PeerIdKademliaAdapter>>();
                    
                    return new PeerIdKademliaAdapter(valueHash256Kademlia, peerIdKeyOperator, logger);
                });
        }
    }
}