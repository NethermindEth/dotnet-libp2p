namespace Libp2p.Core;

public interface IChannelRequest
{
    IProtocol? SubProtocol { get; }
    public TaskCompletionSource CompletionSource { get; }
}
