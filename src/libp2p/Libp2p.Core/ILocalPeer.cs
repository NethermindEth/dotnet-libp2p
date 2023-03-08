namespace Libp2p.Core;

public interface ILocalPeer : IPeer
{
    Task<IRemotePeer> DialAsync(MultiAddr addr, CancellationToken token = default);
    Task<IListener> ListenAsync(MultiAddr addr, CancellationToken token = default);
}
