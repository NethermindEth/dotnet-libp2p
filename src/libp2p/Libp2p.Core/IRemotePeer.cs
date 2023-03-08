namespace Libp2p.Core;

public interface IRemotePeer : IPeer
{
    Task DialAsync<TProtocol>(CancellationToken token = default) where TProtocol : IProtocol;
    Task DisconectAsync();
}
