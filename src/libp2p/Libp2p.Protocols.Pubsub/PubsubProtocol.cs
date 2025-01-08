// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/pubsub
/// </summary>
public abstract class PubsubProtocol : ISessionProtocol
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

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        PeerId? remotePeerId = context.State.RemotePeerId;

        _logger?.LogDebug($"Dialed({context.Id}) {context.State.RemoteAddress}");

        TaskCompletionSource dialTcs = new();
        CancellationToken token = router.OutboundConnection(context.State.RemoteAddress, Id, dialTcs.Task, (rpc) =>
        {
            ValueTask t = channel.WriteSizeAndProtobufAsync(rpc);
            t.AsTask().ContinueWith((t) =>
            {
                if (!t.IsCompletedSuccessfully)
                {
                    _logger?.LogWarning($"Sending RPC failed message to {remotePeerId}: {rpc}");
                }
            });
            _logger?.LogTrace($"Sent message to {remotePeerId}: {rpc}");
        });

        await channel;
        dialTcs.SetResult();
        _logger?.LogDebug($"Finished dial({context.Id}) {context.State.RemoteAddress}");
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        PeerId? remotePeerId = context.State.RemotePeerId;

        _logger?.LogDebug($"Listen({context.Id}) to {context.State.RemoteAddress}");

        TaskCompletionSource listTcs = new();
        TaskCompletionSource dialTcs = new();

        CancellationToken token = router.InboundConnection(context.State.RemoteAddress, Id, listTcs.Task, dialTcs.Task, () =>
        {
            _ = context.DialAsync(this);
            return dialTcs.Task;
        });

        while (!token.IsCancellationRequested)
        {
            Rpc? rpc = await channel.ReadAnyPrefixedProtobufAsync(Rpc.Parser, token);
            if (rpc is null)
            {
                _logger?.LogDebug($"Received a broken message or EOF from {remotePeerId}");
                break;
            }
            else
            {
                _logger?.LogTrace($"Received message from {remotePeerId}: {rpc}");
                router.OnRpc(remotePeerId, rpc);
            }
        }

        listTcs.SetResult();
        _logger?.LogDebug($"Finished({context.Id}) list {context.State.RemoteAddress}");
    }

    public override string ToString() => Id;
}

public class FloodsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.FloodsubProtocolVersion, router, loggerFactory);

public class GossipsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV10, router, loggerFactory);

public class GossipsubProtocolV11(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV11, router, loggerFactory);

public class GossipsubProtocolV12(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV12, router, loggerFactory);
