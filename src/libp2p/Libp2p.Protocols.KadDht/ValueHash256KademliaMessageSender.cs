using System.Threading;
using System.Threading.Tasks;
using Libp2p.Protocols.KadDht.InternalTable.Crypto;
using Libp2p.Protocols.KadDht.InternalTable.Kademlia;
using Microsoft.Extensions.Logging;

namespace Libp2p.Protocols.KadDht;

public class ValueHash256KademliaMessageSender : IKademliaMessageSender<ValueHash256, ValueHash256>
{
    private readonly PeerIdKeyOperator _keyOperator;
    private readonly ILogger<ValueHash256KademliaMessageSender> _logger;
    private readonly KadDhtProtocol _kadDhtProtocol;

    public ValueHash256KademliaMessageSender(
        KadDhtProtocol kadDhtProtocol,
        ILogger<ValueHash256KademliaMessageSender> logger,
        PeerIdKeyOperator keyOperator)
    {
        _kadDhtProtocol = kadDhtProtocol;
        _keyOperator = keyOperator;
        _logger = logger;
    }

    public Task Ping(ValueHash256 target, CancellationToken token)
    {
        var targetPeerId = _keyOperator.GetPeerId(target);
        _logger.LogDebug("Pinging {TargetPeerId}", targetPeerId);
        return Task.CompletedTask;
    }

    public Task<ValueHash256[]> FindNeighbours(ValueHash256 target, ValueHash256 key, CancellationToken token)
    {
        var targetPeerId = _keyOperator.GetPeerId(target);
        _logger.LogDebug("Finding neighbours for {Key} from {TargetPeerId}", key, targetPeerId);
        return Task.FromResult(new ValueHash256[] {});
    }

    public Task SendMessage(ValueHash256 target, KadDhtMessage<ValueHash256> message)
    {
        var targetPeerId = _keyOperator.GetPeerId(target);
        _logger.LogDebug("Sending message to {TargetPeerId}", targetPeerId);
        return Task.CompletedTask; // Placeholder for actual message sending logic
    }
}
