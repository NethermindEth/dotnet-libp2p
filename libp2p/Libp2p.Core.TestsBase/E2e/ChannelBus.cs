// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Core.TestsBase.E2e;

public class ChannelBus(TestContextLoggerFactory? fac = null)
{
    private readonly ILogger? logger = fac?.CreateLogger("bus");

    class ClientChannel
    {
        public required PeerId Client { get; set; }
        public required IChannel Channel { get; set; }
    }

    ConcurrentDictionary<PeerId, Channel<ClientChannel>> channels = [];

    public async IAsyncEnumerable<IChannel> GetIncomingRequests(PeerId serverId, [EnumeratorCancellation] CancellationToken token = default)
    {
        Channel<ClientChannel> col = System.Threading.Channels.Channel.CreateUnbounded<ClientChannel>();

        if (!channels.TryAdd(serverId, col))
        {
            throw new Exception("Test listener with such peer id already exists.");
        }

        logger?.LogDebug($"Listen {serverId}");

        try
        {
            await foreach (ClientChannel item in col.Reader.ReadAllAsync(token))
            {
                logger?.LogDebug($"New request from {item.Client} to {serverId}");
                yield return item.Channel;
            }
        }
        finally
        {
            channels.TryRemove(serverId, out _);
            col.Writer.TryComplete();
            logger?.LogDebug($"Listen end {serverId}");
        }
    }

    public IChannel Dial(PeerId self, PeerId serverId)
    {
        if (!channels.TryGetValue(serverId, out Channel<ClientChannel>? col))
        {
            throw new Exception("Test listener with such peer id does not exist.");
        }

        logger?.LogDebug($"Dial {self} -> {serverId}");

        Channel channel = new();
        _ = col.Writer.WriteAsync(new ClientChannel { Channel = channel.Reverse, Client = self });
        return channel;
    }
}

