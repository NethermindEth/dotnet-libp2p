// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelFactory
{
    IEnumerable<IProtocol> SubProtocols { get; }
    IChannel SubDial(IPeerContext context, IChannelRequest? request = null);

    IChannel SubListen(IPeerContext context, IChannelRequest? request = null);

    Task SubDialAndBind(IChannel parentChannel, IPeerContext context, IChannelRequest? request = null);

    Task SubListenAndBind(IChannel parentChannel, IPeerContext context, IChannelRequest? request = null);



    IChannel SubDial(IPeerContext context, IProtocol protocol)
    {
        return SubDial(context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListen(IPeerContext context, IProtocol protocol)
    {
        return SubListen(context, new ChannelRequest { SubProtocol = protocol });
    }

    Task SubDialAndBind(IChannel parentChannel, IPeerContext context, IProtocol protocol)
    {
        return SubDialAndBind(parentChannel, context, new ChannelRequest { SubProtocol = protocol });
    }

    Task SubListenAndBind(IChannel parentChannel, IPeerContext context, IProtocol protocol)
    {
        return SubListenAndBind(parentChannel, context, new ChannelRequest { SubProtocol = protocol });
    }
}
