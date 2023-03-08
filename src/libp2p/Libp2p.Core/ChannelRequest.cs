namespace Libp2p.Core;

public class ChannelRequest : IChannelRequest
{
    public IProtocol? SubProtocol { get; init; }
    public TaskCompletionSource? CompletionSource { get; init; }

    public override string ToString()
    {
        return $"Requesst for {SubProtocol?.Id ?? "unknown protocol"}";
    }
}
