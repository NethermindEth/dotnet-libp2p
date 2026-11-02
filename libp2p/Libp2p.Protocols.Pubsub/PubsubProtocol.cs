// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Libp2p.Protocols.Pubsub.Dto;
using System.Diagnostics;

#pragma warning disable IDE0130
namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/tree/master/pubsub
/// </summary>
public abstract class PubsubProtocol : ISessionProtocol
#pragma warning restore IDE0130
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
        channel.GetAwaiter().OnCompleted(() => context.Activity?.AddEvent(new ActivityEvent("channel closed")));
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        PeerId remotePeerId = context.State.RemotePeerId!;

        _logger?.LogDebug("Dialed({contextId}) {remoteAddress}", context.Id, context.State.RemoteAddress);

        TaskCompletionSource dialTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PubsubRouter.PubsubPeer? peer = router.OutboundConnection(context.State.RemoteAddress, Id, dialTcs.Task,
            (bytes, token) => channel.WriteAsync(new System.Buffers.ReadOnlySequence<byte>(bytes), token),
            channel.AbortAsync, context.DisconnectAsync);
        if (peer is null)
        {
            await channel.AbortAsync();
            return;
        }
        try
        {
            await channel;
        }
        finally
        {
            dialTcs.TrySetResult();
        }
        context.Activity?.AddEvent(new ActivityEvent($"Finished dial({context.Id}) {context.State.RemoteAddress}"));
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);
        ArgumentNullException.ThrowIfNull(context.State.RemotePeerId);

        PeerId remotePeerId = context.State.RemotePeerId!;

        _logger?.LogDebug("Listen({contextId}) to {remoteAddress}", context.Id, context.State.RemoteAddress);

        TaskCompletionSource listTcs = new();
        (CancellationToken token, Action suppressReconnection) = router.InboundConnection(context.State.RemoteAddress, Id, listTcs.Task, () => context.DialAsync(this), context.DisconnectAsync);
        CancellationToken channelClosed = channel.CancellationToken;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, channelClosed, router.Stopped);
        token = lifetime.Token;

        bool isFirstRpc = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Rpc rpc = await channel.ReadPrefixedProtobufAsync(Rpc.Parser, router.MaxRpcBytes, token);
                _logger?.LogTrace("Received message from {remotePeerId}: {rpc}", remotePeerId, rpc);
                context.Activity?.AddEvent(new ActivityEvent($"Received message from {remotePeerId}: {rpc}"));
                await router.OnRpcAsync(remotePeerId, rpc, Id, isFirstRpc, token);
                isFirstRpc = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger?.LogDebug("RPC listener stopped for {remotePeerId}", remotePeerId);
        }
        catch (ChannelClosedException)
        {
            _logger?.LogDebug("RPC channel closed by {remotePeerId}", remotePeerId);
            context.Activity?.AddEvent(new ActivityEvent($"RPC channel closed by {remotePeerId}"));
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or InvalidProtocolBufferException)
        {
            _logger?.LogDebug(e, "Invalid RPC from {remotePeerId}: {message}", remotePeerId, e.Message);
            context.Activity?.AddEvent(new ActivityEvent($"Invalid RPC from {remotePeerId}: {e.Message}"));
            context.Activity?.SetStatus(ActivityStatusCode.Error);
            suppressReconnection();
            await context.DisconnectAsync();
        }
        catch (Exception e)
        {
            context.Activity?.AddEvent(new ActivityEvent($"Exception: {e.Message}"));
            context.Activity?.SetStatus(ActivityStatusCode.Error);
        }
        finally
        {
            listTcs.SetResult();
            context.Activity?.AddEvent(new ActivityEvent($"Finished({context.Id}) list {context.State.RemoteAddress}"));
        }
    }

    public override string ToString() => Id;
}

public class FloodsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.FloodsubProtocolVersion, router, loggerFactory);

public class GossipsubProtocol(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV10, router, loggerFactory);

public class GossipsubProtocolV11(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV11, router, loggerFactory);

public class GossipsubProtocolV12(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV12, router, loggerFactory);

public class GossipsubProtocolV13(PubsubRouter router, ILoggerFactory? loggerFactory = null) : PubsubProtocol(PubsubRouter.GossipsubProtocolVersionV13, router, loggerFactory);
