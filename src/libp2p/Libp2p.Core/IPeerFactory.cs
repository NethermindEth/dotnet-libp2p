namespace Libp2p.Core;

public interface IPeerFactory
{
    ILocalPeer Create(Identity? identity = default);
}
