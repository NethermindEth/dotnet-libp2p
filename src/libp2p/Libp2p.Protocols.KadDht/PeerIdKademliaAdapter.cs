using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace Libp2p.Protocols.KadDht;

public class PeerIdKademliaAdapter : IKademlia<PeerId, ValueHash256>
{
    private readonly IKademlia<ValueHash256, ValueHash256> _innerKademlia;
    private readonly PeerIdKeyOperator _keyOperator;
    private readonly ILogger<PeerIdKademliaAdapter> _logger;

    public PeerIdKademliaAdapter(
        IKademlia<ValueHash256, ValueHash256> innerKademlia,
        PeerIdKeyOperator keyOperator,
        ILogger<PeerIdKademliaAdapter> logger)
    {
        _innerKademlia = innerKademlia;
        _keyOperator = keyOperator;
        _logger = logger;
    }

    public void AddOrRefresh(PeerId node)
    {
        var key = _keyOperator.GetKey(node);
        _innerKademlia.AddOrRefresh(key);
    }

    public void Remove(PeerId node)
    {
        var key = _keyOperator.GetKey(node);
        _innerKademlia.Remove(key);
    }

    public Task<PeerId[]> LookupNodesClosest(ValueHash256 key, CancellationToken? token, bool b, int? k = null)
    {
        return _innerKademlia.LookupNodesClosest(key, token, k: k);
    }

    public PeerId[] GetKNeighbour(ValueHash256 target, PeerId? excluding = default, bool excludeSelf = false)
    {
        var excludingKey = excluding != null ? _keyOperator.GetKey(excluding) : default;
        return _innerKademlia.GetKNeighbour(target, excludingKey, excludeSelf);
    }

    public Task Bootstrap(CancellationToken token)
    {
        return _innerKademlia.Bootstrap(token);
    }

    public Task Run(CancellationToken token)
    {
        return _innerKademlia.Run(token);
    }

    public event EventHandler<PeerId>? OnNodeAdded
    {
        add => _innerKademlia.OnNodeAdded += (sender, key) => value?.Invoke(sender, _keyOperator.GetPeerId(key));
        remove => _innerKademlia.OnNodeAdded -= (sender, key) => value?.Invoke(sender, _keyOperator.GetPeerId(key));
    }

    public IEnumerable<PeerId> IterateNodes()
    {
        foreach (var key in _innerKademlia.IterateNodes())
        {
            yield return _keyOperator.GetPeerId(key);
        }
    }
}
