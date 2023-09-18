// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols.Pubsub;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/pubsub
/// </summary>
public abstract class PubsubProtocol : IProtocol
{
    private readonly ILogger? _logger;
    private readonly PubsubRouter router;

    public string Id { get; }

    public PubsubProtocol(string protocolId, PubsubRouter router, ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger(GetType());
        Id = protocolId;
        this.router = router;
    }

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        string peerId = context.RemotePeer.Address.At(MultiaddrEnum.P2p)!;
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

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        string peerId = context.RemotePeer.Address.At(MultiaddrEnum.P2p)!;
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

public class FloodsubProtocol : PubsubProtocol
{
    public FloodsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : base(PubsubRouter.FloodsubProtocolVersion, router, loggerFactory)
    {
    }
}

public class GossipsubProtocol : PubsubProtocol
{
    public GossipsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : base(PubsubRouter.GossipsubProtocolVersionV10, router, loggerFactory)
    {
    }
}

public class GossipsubProtocolV11 : PubsubProtocol
{
    public GossipsubProtocolV11(PubsubRouter router, ILoggerFactory? loggerFactory = null) : base(PubsubRouter.GossipsubProtocolVersionV11, router, loggerFactory)
    {
    }
}

public class GossipsubProtocolV12 : PubsubProtocol
{
    public GossipsubProtocolV12(PubsubRouter router, ILoggerFactory? loggerFactory = null) : base(PubsubRouter.GossipsubProtocolVersionV12, router, loggerFactory)
    {
    }
}
