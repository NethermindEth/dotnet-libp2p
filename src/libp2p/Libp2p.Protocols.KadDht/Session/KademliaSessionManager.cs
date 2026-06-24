// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Libp2p.Protocols.KadDht.Kademlia;
using Nethermind.Kademlia;

namespace Libp2p.Protocols.KadDht;

public sealed class KademliaSessionManager : ISessionManager
{
    private readonly SessionOptions _options;
    private readonly ILoggerFactory _logFactory;
    private readonly ILogger<KademliaSessionManager> _log;

    private readonly IKeyOperator<PublicKey, TestNode, ValueHash256> _keyOperator;
    private readonly IKademliaMessageSender<PublicKey, TestNode> _transportMessageSender;
    private readonly Nethermind.Kademlia.IKademliaMessageSender<PublicKey, TestNode> _kademliaMessageSender;
    private readonly KademliaConfig<TestNode> _config;
    private readonly INodeHashProvider<TestNode, ValueHash256> _nodeHashProvider;
    private readonly IRoutingTable<TestNode, ValueHash256> _routingTable;
    private readonly NodeHealthTracker<PublicKey, TestNode, ValueHash256> _nodeHealthTracker;
    private readonly ILookupAlgo<TestNode, ValueHash256> _lookupAlgo;
    private readonly Nethermind.Kademlia.Kademlia<PublicKey, TestNode, ValueHash256> _kad;

    private readonly CancellationTokenSource _cts = new();

    public KademliaSessionManager(
        SessionOptions options,
        IKademliaMessageSender<PublicKey, TestNode> messageSender,
        ILoggerFactory? logFactory = null)
    {
        _options = options;
        _logFactory = logFactory ?? NullLoggerFactory.Instance;
        _log = _logFactory.CreateLogger<KademliaSessionManager>();

        _keyOperator = new PublicKeyKeyOperator();
        _transportMessageSender = messageSender;
        _kademliaMessageSender = new MessageSenderAdapter(_transportMessageSender);

        _config = new KademliaConfig<TestNode>
        {
            CurrentNodeId = new TestNode(new Nethermind.Libp2p.Core.PeerId(RandomNumberGenerator.GetBytes(32)))
        };
        if (options.KSize is int k) _config.KSize = k;
        if (options.RefreshInterval is TimeSpan r) _config.RefreshInterval = r;

        var distance = new ValueHash256Distance();
        _nodeHashProvider = new FromKeyNodeHashProvider<PublicKey, TestNode, ValueHash256>(_keyOperator);
        _routingTable = new KBucketTree<TestNode, ValueHash256>(_config, _nodeHashProvider, distance);
        _nodeHealthTracker = new NodeHealthTracker<PublicKey, TestNode, ValueHash256>(_config, _routingTable, _nodeHashProvider, _kademliaMessageSender);
        _lookupAlgo = new LookupKNearestNeighbour<PublicKey, TestNode, ValueHash256>(_routingTable, _nodeHashProvider, distance, _nodeHealthTracker, _config);

        _kad = new Nethermind.Kademlia.Kademlia<PublicKey, TestNode, ValueHash256>(_keyOperator, _kademliaMessageSender, _routingTable, _lookupAlgo, _nodeHealthTracker, _config);
    }

    public async Task BootstrapAsync(CancellationToken ct)
    {
        _log.LogInformation("Kademlia bootstrap starting. Bootstrap peers: {Count}", _options.BootstrapMultiAddresses.Count);
        await _kad.Bootstrap(ct).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        _log.LogInformation("Kademlia run loop starting.");
        await _kad.Run(linked.Token).ConfigureAwait(false);
    }

    public async Task<TNode[]> DiscoverAsync<TNode>(object targetKey, CancellationToken ct)
    {
        if (targetKey is not PublicKey key)
            throw new ArgumentException("targetKey must be Kademlia.PublicKey for this session.", nameof(targetKey));

        ValueHash256 currentNodeIdAsHash = _keyOperator.GetNodeHash(_config.CurrentNodeId);

        var nodes = await _lookupAlgo.Lookup(
            _keyOperator.GetKeyHash(key),
            _config.KSize,
            async (nextNode, token) =>
            {
                if (_keyOperator.GetNodeHash(nextNode).Equals(currentNodeIdAsHash))
                {
                    ValueHash256 keyHash = _keyOperator.GetKeyHash(key);
                    return _routingTable.GetKNearestNeighbour(keyHash);
                }
                return await _kademliaMessageSender.FindNeighbours(nextNode, key, token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        if (typeof(TNode) == typeof(TestNode))
            return (TNode[])(object)nodes;

        throw new NotSupportedException("Provide an adapter to map TestNode to your domain node type.");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("Kademlia stopping.");
        _cts.Cancel();
        _nodeHealthTracker.Dispose();
        _cts.Dispose();
        return Task.CompletedTask;
    }
}
