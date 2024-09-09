// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Threading.Channels;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class ChannelBus
{
    Dictionary<PeerId, Channel<IChannel>> channels = [];

    public IAsyncEnumerable<IChannel> GetIncomingRequests(PeerId serverId)
    {
        Channel<IChannel> col = System.Threading.Channels.Channel.CreateUnbounded<IChannel>();

        if (!channels.TryAdd(serverId, col))
        {
            throw new Exception("Test listener with such peer id alread exists.");
        }

        return col.Reader.ReadAllAsync();
    }

    public IChannel Dial(PeerId self, PeerId serverId)
    {
        if (!channels.TryGetValue(serverId, out Channel<IChannel>? col))
        {
            throw new Exception("Test listener with such peer id does not exist.");
        }

        Channel channel = new();
        _ = col.Writer.WriteAsync(channel.Reverse);
        return channel;
    }
}

