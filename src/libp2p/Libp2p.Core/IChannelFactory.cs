// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public interface IChannelFactory
{
    IEnumerable<IProtocol> SubProtocols { get; }
    IChannel SubDial(IChannelRequest? request = null);

    IChannel SubListen(IChannelRequest? request = null);

    Task SubDialAndBind(IChannel parentChannel, IChannelRequest? request = null);

    Task SubListenAndBind(IChannel parentChannel, IChannelRequest? request = null);


    IChannel SubDial(IProtocol protocol)
    {
        return SubDial(new ChannelRequest { SubProtocol = protocol });
    }

    IChannel SubListen(IProtocol protocol)
    {
        return SubListen(new ChannelRequest { SubProtocol = protocol });
    }

    Task SubDialAndBind(IChannel parentChannel, IProtocol protocol)
    {
        return SubDialAndBind(parentChannel, new ChannelRequest { SubProtocol = protocol });
    }

    Task SubListenAndBind(IChannel parentChannel, IProtocol protocol)
    {
        return SubListenAndBind(parentChannel, new ChannelRequest { SubProtocol = protocol });
    }
}
