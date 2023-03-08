namespace Libp2p.Core;

public abstract class SymetricProtocol
{
    public Task DialAsync(IChannel channel, IChannelFactory channelFactory, IPeerContext context)
    {
        return ConnectAsync(channel, channelFactory, context, false);
    }

    public Task ListenAsync(IChannel channel, IChannelFactory channelFactory, IPeerContext context)
    {
        return ConnectAsync(channel, channelFactory, context, true);
    }

    protected abstract Task ConnectAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context, bool isListener);
}
