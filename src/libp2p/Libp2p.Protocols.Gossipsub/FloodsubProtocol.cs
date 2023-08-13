// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.GossipSub.Dto;
using Libp2p.Protocols.Floodsub;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/pubsub
/// </summary>
public class FloodsubProtocol : IProtocol
{
    private readonly ILogger? _logger;
    private readonly PubsubRouter router;

    public virtual string Id => "/floodsub/1.0.0";

    public FloodsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger(GetType());
        this.router = router;
    }

    public async Task DialAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        string peerId = context.RemotePeer.Address.At(Core.Enums.Multiaddr.P2p)!;
        _logger?.LogDebug($"Dialed({context.Id}) {context.RemotePeer.Address}");


        CancellationToken token = router.OutboundConnection(peerId, Id, (rpc) =>
        {
            _ = channel.WritePrefixedProtobufAsync(rpc);
        });

        try
        {
            await Task.Delay(-1, token);
        }
        catch
        {

        }
        _logger?.LogDebug($"Finished dial({context.Id}) {context.RemotePeer.Address}");

    }

    public async Task ListenAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context)
    {
        string peerId = context.RemotePeer.Address.At(Core.Enums.Multiaddr.P2p)!;
        _logger?.LogDebug($"Listen({context.Id}) to {context.RemotePeer.Address}");

        CancellationToken token = router.InboundConnection(peerId, Id, () =>
        {
            context.SubDialRequests.Add(new ChannelRequest { SubProtocol = this });
        });
        while (!token.IsCancellationRequested)
        {
            Rpc? rpc = await channel.ReadPrefixedProtobufAsync(Rpc.Parser, token);
            router.OnRpc(peerId, rpc);
        }
        _logger?.LogDebug($"Finished({context.Id}) list {context.RemotePeer.Address}");
    }
}
