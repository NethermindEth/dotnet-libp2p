namespace Libp2p.Core;

public interface IPeerContext
{
    IPeer LocalPeer { get; }
    IPeer RemotePeer { get; }

    IProtocol[] ApplayerProtocols { get; }
    MultiAddr RemoteEndpoint { get; set; }
    MultiAddr LocalEndpoint { get; set; }
    IPeerContext Fork();
}
