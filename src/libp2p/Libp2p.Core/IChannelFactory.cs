using System.Collections.Concurrent;

namespace Libp2p.Core;

public interface IChannelFactory
{
    IEnumerable<IProtocol> SubProtocols { get; }
    BlockingCollection<IChannelRequest> SubDialRequests { get; }

    IChannel SubDial(IPeerContext context, IChannelRequest? request = null);

    IChannel SubListen(IPeerContext context, IChannelRequest? request = null);

    IChannel SubDialAndBind(IChannel parentChannel, IPeerContext context, IChannelRequest? request = null);

    IChannel SubListenAndBind(IChannel parentChannel, IPeerContext context, IChannelRequest? request = null);


    IChannel SubDial(IPeerContext context, IProtocol protocol)
    {
        return SubDial(context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListen(IPeerContext context, IProtocol protocol)
    {
        return SubListen(context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubDialAndBind(IChannel parentChannel, IPeerContext context, IProtocol protocol)
    {
        return SubDialAndBind(parentChannel, context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListenAndBind(IChannel parentChannel, IPeerContext context, IProtocol protocol)
    {
        return SubListenAndBind(parentChannel, context, new ChannelRequest { SubProtocol = protocol });
    }

    void Connected(IRemotePeer peer);
    event RemotePeerConnected OnRemotePeerConnection;
}
