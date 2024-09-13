// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class ChannelBus(TestContextLoggerFactory? fac = null)
{
    ILogger? logger = fac?.CreateLogger("bus");

    Dictionary<PeerId, Channel<IChannel>> channels = [];

    public async IAsyncEnumerable<IChannel> GetIncomingRequests(PeerId serverId)
    {
        Channel<IChannel> col = System.Threading.Channels.Channel.CreateUnbounded<IChannel>();

        if (!channels.TryAdd(serverId, col))
        {
            throw new Exception("Test listener with such peer id alread exists.");
        }
        logger?.LogDebug($"Listen {serverId}");

        await foreach (var item in col.Reader.ReadAllAsync())
        {
            logger?.LogDebug($"New request to {serverId}");
            yield return item;
        }
        logger?.LogDebug($"Listen end {serverId}");
    }

    public IChannel Dial(PeerId self, PeerId serverId)
    {
        if (!channels.TryGetValue(serverId, out Channel<IChannel>? col))
        {
            throw new Exception("Test listener with such peer id does not exist.");
        }

        logger?.LogDebug($"Dial {self} -> {serverId}");

        Channel channel = new();
        _ = col.Writer.WriteAsync(channel.Reverse);
        return channel;
    }
}

