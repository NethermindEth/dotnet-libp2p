// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Diagnostics;

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
        channel.GetAwaiter().OnCompleted(() => context.Activity.AddEvent(new ActivityEvent("channel closed")));
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        PeerId? remotePeerId = context.State.RemotePeerId;

        _logger?.LogDebug($"Dialed({context.Id}) {context.State.RemoteAddress}");

        TaskCompletionSource dialTcs = new();
        CancellationToken token = router.OutboundConnection(context.State.RemoteAddress, Id, dialTcs.Task, (rpc) =>
        {
            channel.WriteSizeAndProtobufAsync(rpc).AsTask().ContinueWith((t) =>
            {
                if (!t.IsCompletedSuccessfully)
                {
                    context.Activity?.AddEvent(new ActivityEvent($"Sending RPC failed message to {remotePeerId}: {rpc}"));
                }
            });
            context.Activity?.AddEvent(new ActivityEvent($"Sent message to {remotePeerId}: {rpc}"));
        });

        await channel;
        dialTcs.SetResult();
        context.Activity?.AddEvent(new ActivityEvent($"Finished dial({context.Id}) {context.State.RemoteAddress}"));
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

        try
        {
            while (!token.IsCancellationRequested)
            {
                Rpc? rpc = await channel.ReadPrefixedProtobufAsync(Rpc.Parser, token);
                if (rpc is null)
                {
                    _logger?.LogDebug($"Received a broken message or EOF from {remotePeerId}");
                    context.Activity?.AddEvent(new ActivityEvent($"Received a broken message or EOF from {remotePeerId}"));
                    break;
                }
                else
                {
                    //_logger?.LogTrace($"Received message from {remotePeerId}: {rpc}");
                    router.OnRpc(remotePeerId, rpc);
                    context.Activity?.AddEvent(new ActivityEvent($"Received message to {remotePeerId}: {rpc}"));
                }
            }
        }
        catch (Exception e)
        {
            context.Activity?.AddEvent(new ActivityEvent($"Exception: {e.Message}"));
            context.Activity.SetStatus(ActivityStatusCode.Error);
        }

        listTcs.SetResult();
        context.Activity?.AddEvent(new ActivityEvent($"Finished({context.Id}) list {context.State.RemoteAddress}"));
    }

    public override string ToString() => Id;
}

public class FloodsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.FloodsubProtocolVersion, router, loggerFactory);

public class GossipsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV10, router, loggerFactory);

public class GossipsubProtocolV11(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV11, router, loggerFactory);

public class GossipsubProtocolV12(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV12, router, loggerFactory);
