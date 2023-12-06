// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelFactory
{
    IEnumerable<IId> SubProtocols { get; }
    IChannel SubDial(IPeerContext context, IChannelRequest? request = null);

    IChannel SubListen(IPeerContext context, IChannelRequest? request = null);

    IChannel SubDialAndBind(IChannel parentChannel, IPeerContext context, IChannelRequest? request = null);

    IChannel SubListenAndBind(IChannel parentChannel, IPeerContext context, IChannelRequest? request = null);

    IChannel SubDial(IPeerContext context, IId protocol)
    {
        return SubDial(context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListen(IPeerContext context, IId protocol)
    {
        return SubListen(context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubDialAndBind(IChannel parentChannel, IPeerContext context, IId protocol)
    {
        return SubDialAndBind(parentChannel, context, new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListenAndBind(IChannel parentChannel, IPeerContext context, IId protocol)
    {
        return SubListenAndBind(parentChannel, context, new ChannelRequest { SubProtocol = protocol });
    }
}
