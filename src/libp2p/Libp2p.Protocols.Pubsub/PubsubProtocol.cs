// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Multiformats.Address.Protocols;
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
        try
        {
            string peerId = context.RemotePeer.Address.Get<P2P>().ToString()!;
            _logger?.LogDebug($"Dialed({context.Id}) {context.RemotePeer.Address}");

            TaskCompletionSource dialTcs = new();
            CancellationToken token = router.OutboundConnection(context.RemotePeer.Address, Id, dialTcs.Task, (rpc) =>
            {
                var t = channel.WriteSizeAndProtobufAsync(rpc);
                t.AsTask().ContinueWith((t) =>
                {
                    if (!t.IsCompletedSuccessfully)
                    {
                        _logger?.LogWarning($"Sending RPC failed message to {peerId}: {rpc}");
                    }
                });
                _logger?.LogTrace($"Sent message to {peerId}: {rpc}");
            });

            await channel;
            dialTcs.SetResult();
            _logger?.LogDebug($"Finished dial({context.Id}) {context.RemotePeer.Address}");
        }
        catch
        {

        }
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        try
        {
            string peerId = context.RemotePeer.Address.Get<P2P>().ToString()!;
            _logger?.LogDebug($"Listen({context.Id}) to {context.RemotePeer.Address}");

            TaskCompletionSource listTcs = new();
            TaskCompletionSource dialTcs = new();

            CancellationToken token = router.InboundConnection(context.RemotePeer.Address, Id, listTcs.Task, dialTcs.Task, () =>
            {
                context.SubDialRequests.Add(new ChannelRequest { SubProtocol = this });
                return dialTcs.Task;
            });

            while (!token.IsCancellationRequested)
            {
                Rpc? rpc = await channel.ReadAnyPrefixedProtobufAsync(Rpc.Parser, token);
                if (rpc is null)
                {
                    _logger?.LogDebug($"Received a broken message or EOF from {peerId}");
                    break;
                }
                else
                {
                    _logger?.LogTrace($"Received message from {peerId}: {rpc}");
                    _ = router.OnRpc(peerId, rpc);
                }
            }
            listTcs.SetResult();
            _logger?.LogDebug($"Finished({context.Id}) list {context.RemotePeer.Address}");
        }
        catch
        {

        }
    }

    public override string ToString()
    {
        return Id;
    }
}

public class FloodsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.FloodsubProtocolVersion, router, loggerFactory);

public class GossipsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV10, router, loggerFactory);

public class GossipsubProtocolV11(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV11, router, loggerFactory);

public class GossipsubProtocolV12(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV12, router, loggerFactory);
