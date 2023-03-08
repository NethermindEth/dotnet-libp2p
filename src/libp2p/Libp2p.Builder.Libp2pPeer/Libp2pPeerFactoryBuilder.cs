using Libp2p.Core;
using Libp2p.Protocols;

namespace Libp2p.Builder;

public class Libp2pPeerFactoryBuilder : PeerFactoryBuilderBase<Libp2pPeerFactoryBuilder, Libp2pPeerFactory>
{
    public static Libp2pPeerFactoryBuilder Instance => new();

    protected override Libp2pPeerFactoryBuilder BuildTransportLayer()
    {
        return Over<IpTcpProtocol>()
            .Select<MultistreamProtocol>()
            .Over<PlainTextProtocol>()
            .Select<MultistreamProtocol>()
            .Over<YamuxProtocol>()
            .Select<MultistreamProtocol>();
    }
}
