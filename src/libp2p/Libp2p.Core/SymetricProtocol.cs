// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

namespace Nethermind.Libp2p.Core;

public abstract class SymmetricProtocol
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
