// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Libp2p.Protocols.KadDht.Kademlia;
using Libp2p.Protocols.KadDht.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;

namespace Libp2p.Protocols.KadDht.Integration;

/// <summary>One runtime per local peer; only the request handler participates in negotiation.</summary>
internal sealed class DhtRuntime : IDisposable, IAsyncDisposable
{
    public required SharedDhtState State { get; init; }
    public required LibP2pKademliaMessageSender Sender { get; init; }
    public required KadDhtProtocol Protocol { get; init; }
    public required DhtClient Client { get; init; }
    public required KadDhtProtocolExtensions.DhtHandlerServices Handler { get; init; }

    internal static DhtRuntime Create(ILocalPeer peer, IServiceProvider sp, IEnumerable<DhtNode>? bootstrap = null, SharedDhtState? state = null, LibP2pKademliaMessageSender? sender = null)
    {
        var options = sp.GetRequiredService<KadDhtOptions>();
        var loggerFactory = sp.GetService<ILoggerFactory>();
        var peerStore = sp.GetService<PeerStore>();
        var values = sp.GetRequiredService<IValueStore>();
        var providers = sp.GetRequiredService<IProviderStore>();
        var validator = sp.GetRequiredService<IRecordValidator>();
        state ??= new SharedDhtState(values, loggerFactory, options.RecordTtl);
        state.LocalPeerKey = new PublicKey(peer.Identity.PeerId.Bytes);
        state.KValue = options.KSize;
        sender ??= new LibP2pKademliaMessageSender(options, peer, loggerFactory,
            onPeerDiscovered: node => ServiceCollectionExtensions.StorePeerAddresses(node, peerStore),
            onPingResponse: node => state.AddNodeCallback?.Invoke(node));
        var protocol = new KadDhtProtocol(peer, sender, sender, options, values, providers, loggerFactory, validator, bootstrap);
        state.SetRoutingTable(protocol.RoutingTable!);
        state.AddNodeCallback = protocol.AddNode;
        return new DhtRuntime
        {
            State = state,
            Sender = sender,
            Protocol = protocol,
            Client = new DhtClient(state, sender, values, validator, options, loggerFactory),
            Handler = new(options, values, providers, validator,
                key => ServiceCollectionExtensions.EnrichPeerAddresses(state.GetKNearestPeers(key), peerStore),
                node =>
                {
                    protocol.AddNode(ServiceCollectionExtensions.EnrichNodeFromPeerStore(node, peerStore));
                    ServiceCollectionExtensions.StorePeerAddresses(node, peerStore);
                })
        };
    }

    private readonly Lock _lifecycle = new();
    private Task? _disposal;

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            return new(_disposal ??= Task.Run(async () =>
            {
                try { await Client.DisposeAsync().ConfigureAwait(false); }
                finally { Protocol.Dispose(); }
            }));
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
