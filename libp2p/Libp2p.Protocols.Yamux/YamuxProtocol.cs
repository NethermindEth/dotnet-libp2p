// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Yamux;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Libp2p.Protocols.Yamux.Tests")]

namespace Nethermind.Libp2p.Protocols;

public partial class YamuxProtocol : SymmetricProtocol, IConnectionProtocol
{
    public const int ProtocolInitialWindowSize = 256 * 1024;

    private const int HeaderLength = 12;
    private const int PingDelay = 30_000;
    private static readonly TimeSpan ControlWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum number of concurrently tracked streams per session. Bounds the
    /// state a peer can force us to allocate: the libp2p spec asks peers to
    /// keep the unacknowledged backlog at 256, and rust-yamux caps streams at
    /// 512 by default. Locally initiated streams bypass the cap; only inbound
    /// SYNs are rejected with RST once the table is full.
    /// </summary>
    private const int MaxStreamCount = 512;

    private const string NoSession = "pending";
    public YamuxProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null,
        YamuxWindowSettings? windowSettings = null, TimeProvider? timeProvider = null, TimeSpan? closedStreamIdleTimeout = null)
    {
        if (closedStreamIdleTimeout is { } timeout &&
            (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMilliseconds(uint.MaxValue - 1)))
            throw new ArgumentOutOfRangeException(nameof(closedStreamIdleTimeout),
                "The timeout must be positive and no longer than 4,294,967,294 milliseconds.");

        if (windowSettings is { InitialWindowSize: < ProtocolInitialWindowSize })
            throw new ArgumentOutOfRangeException(nameof(windowSettings),
                "The initial receive window cannot be smaller than the 256 KiB Yamux default.");

        multiplexerSettings?.Add(this);
        _logger = loggerFactory?.CreateLogger<YamuxProtocol>();
        _windowSettings = windowSettings ?? new YamuxWindowSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _closedStreamIdleTimeout = closedStreamIdleTimeout;
    }

    private readonly ILogger? _logger;
    private readonly YamuxWindowSettings _windowSettings;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan? _closedStreamIdleTimeout;

    public string Id => "/yamux/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
    {
        using var scope = _logger?.BeginScope("Context id {ctx}", context.Id);
        _logger?.LogInformation("Ctx({ctx}): {mode} {peer}", context.Id, isListener ? "Listen" : "Dial", context.State.RemoteAddress);

        TaskAwaiter downChannelAwaiter = channel.GetAwaiter();
        channel.GetAwaiter().OnCompleted(() => context.Activity?.AddEvent(new ActivityEvent("channel closed")));

        ConcurrentDictionary<int, ChannelState> channels = [];
        INewSessionContext? session = null;
        Timer? timer = null;

        try
        {
            int streamIdCounter = isListener ? 2 : 1;

            SemaphoreSlim waitForSession = new(0, 1);
            if (!isListener)
            {
                session = context.UpgradeToSession();
                _logger?.LogInformation("Ctx({ctx}): Session created by dialer for {peer}", session.Id, session.State.RemoteAddress);
                waitForSession.Release();
            }

            _ = Task.Run(async () =>
            {
                await waitForSession.WaitAsync();
                if (session is null)
                {
                    throw new Libp2pException("Session was not initialized.");
                }

                uint pingCounter = 0;

                timer = new((s) =>
                {
                    _ = WriteHeaderAsync(session.Id, channel, new YamuxHeader { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = (int)(++pingCounter % int.MaxValue) });
                }, null, PingDelay, PingDelay);

                foreach (UpgradeOptions request in session.DialRequests)
                {
                    int streamId = streamIdCounter;
                    Interlocked.Add(ref streamIdCounter, 2);

                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Dialing with protocol {proto}", session.Id, streamId, request.SelectedProtocol?.Id);
                    CreateUpchannel(session.Id, streamId, YamuxHeaderFlags.Syn, request);
                }
            });

            while (!downChannelAwaiter.IsCompleted)
            {
                YamuxHeader header = await ReadHeaderAsync(session?.Id ?? NoSession, channel, channel.CancellationToken);
                ReadOnlySequence<byte> data = default;

                if (header.Version != 0 || header.Type > YamuxHeaderType.GoAway)
                {
                    _logger?.LogWarning("Ctx({ctx}): Bad packet received, version: {version}, type: {type}",
                        session?.Id ?? NoSession, header.Version, header.Type);
                    await WriteGoAwayAsync(session?.Id ?? NoSession, channel, SessionTerminationCode.ProtocolError);
                    return;
                }

                if ((header.Type == YamuxHeaderType.Data &&
                     (header.Length < 0 || header.Length > _windowSettings.MaxWindowSize)) ||
                    (header.Type == YamuxHeaderType.WindowUpdate && header.Length < 0))
                {
                    await WriteGoAwayAsync(session?.Id ?? NoSession, channel, SessionTerminationCode.ProtocolError);
                    return;
                }

                if ((header.StreamID == 0) != (header.Type is YamuxHeaderType.Ping or YamuxHeaderType.GoAway) ||
                    ((header.Flags & YamuxHeaderFlags.Syn) != 0 && header.StreamID != 0 &&
                     (header.StreamID & 1) != (isListener ? 1 : 0)))
                {
                    await WriteGoAwayAsync(session?.Id ?? NoSession, channel, SessionTerminationCode.ProtocolError);
                    return;
                }

                if (header.StreamID is 0)
                {
                    if (header.Type == YamuxHeaderType.Ping)
                    {
                        if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                        {
                            _ = WriteHeaderAsync(session?.Id ?? NoSession, channel,
                                new YamuxHeader
                                {
                                    Flags = YamuxHeaderFlags.Ack,
                                    Type = YamuxHeaderType.Ping,
                                    Length = header.Length,
                                });

                            _logger?.LogDebug("Ctx({ctx}): Ping received and acknowledged", session?.Id ?? NoSession);
                        }
                        continue;
                    }

                    if (header.Type == YamuxHeaderType.GoAway)
                    {
                        _logger?.LogDebug("Ctx({ctx}): Closing all streams", session?.Id ?? NoSession);

                        foreach (ChannelState channelState in channels.Values)
                        {
                            if (channelState.Channel is not null)
                            {
                                await channelState.Channel.CloseAsync();
                            }
                        }

                        break;
                    }

                    continue;
                }

                if (isListener && session is null)
                {
                    try
                    {
                        session = context.UpgradeToSession();
                    }
                    catch (SessionExistsException)
                    {
                        _logger?.LogDebug("Ctx({ctx}): Rejected redundant session for {peer}", context.Id, context.State.RemoteAddress);
                        return;
                    }
                    _logger?.LogInformation("Ctx({ctx}): Session created by listener for {peer}", session.Id, session.State.RemoteAddress);
                    waitForSession.Release();
                }

                if (session is null)
                {
                    throw new Libp2pException("Session was not initialized.");
                }

                if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn && !channels.ContainsKey(header.StreamID))
                {
                    if (channels.Count >= MaxStreamCount)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Rejected, stream table is full", session.Id, header.StreamID);
                        await WriteHeaderAsync(session.Id, channel,
                            new YamuxHeader
                            {
                                Flags = YamuxHeaderFlags.Rst,
                                Type = YamuxHeaderType.WindowUpdate,
                                StreamID = header.StreamID
                            });
                        continue;
                    }
                    CreateUpchannel(session.Id, header.StreamID, YamuxHeaderFlags.Ack, new UpgradeOptions());
                }

                if (!channels.TryGetValue(header.StreamID, out ChannelState? stream))
                {
                    if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                    {
                        await channel.ReadAsync(header.Length);
                    }
                    _logger?.LogDebug("Ctx({ctx}): Stream {stream id}: Ignored for closed stream", session.Id, header.StreamID);
                    continue;
                }

                if (header.Type == YamuxHeaderType.Data && header.Length > stream.LocalWindow.Available)
                {
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Data length > windows size: {length} > {window size}", session.Id,
                        header.StreamID, header.Length, stream.LocalWindow.Available);
                    await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.ProtocolError);
                    return;
                }

                if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst)
                {
                    stream.AbortOutbound();
                    if (stream.Channel is { } resetChannel)
                        _ = resetChannel.AbortAsync();
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Reset", session.Id, header.StreamID);
                    if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                        await channel.ReadAsync(header.Length).OrThrow();
                    continue;
                }

                if (header is { Type: YamuxHeaderType.Data, Length: not 0 })
                {
                    int available = stream.LocalWindow.Available;
                    data = await channel.ReadAsync(header.Length).OrThrow();

                    bool spent = stream.LocalWindow.TrySpend((int)data.Length);
                    if (!spent)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Local window spent out of budget", session.Id, header.StreamID);
                        await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.InternalError);
                        return;
                    }

                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Local spent window, was {available}, became {new}", session.Id,
                               header.StreamID, available, stream.LocalWindow.Available);

                    int dataLength = (int)data.Length;
                    _ = stream.Channel!.WriteAsync(data).AsTask().ContinueWith((t) =>
                    {
                        if (!t.IsCompletedSuccessfully)
                        {
                            Exception? failure = t.Exception;
                            _logger?.LogWarning(failure, "Ctx({ctx}), stream {stream id}: Failed to send upstream", session.Id, header.StreamID);
                            return;
                        }

                        ExtendWindow(channel, session.Id, header.StreamID, t.Result, dataLength);
                    });
                }

                if (header.Type == YamuxHeaderType.WindowUpdate && header.Length != 0)
                {
                    int oldSize = stream.RemoteWindow.Available;
                    if (!stream.RemoteWindow.TryExtend(header.Length, out int newSize))
                    {
                        await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.ProtocolError);
                        return;
                    }
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Window update received: {old} => {new}", session.Id, header.StreamID, oldSize, newSize);
                }

                if ((header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
                {
                    _ = stream.Channel?.WriteEofAsync();
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Finish receiving", session.Id, header.StreamID);
                }
            }

            _ = WriteGoAwayAsync(session?.Id ?? NoSession, channel, SessionTerminationCode.Ok);

            void CreateUpchannel(string contextId, int streamId, YamuxHeaderFlags initiationFlag, UpgradeOptions upgradeOptions)
            {
                bool isListenerChannel = isListener ^ (streamId % 2 == 0);

                IChannel upChannel;

                if (isListenerChannel)
                {
                    upChannel = session.Upgrade(upgradeOptions with { ModeOverride = UpgradeModeOverride.Listen });
                }
                else
                {
                    upChannel = session.Upgrade(upgradeOptions with { ModeOverride = UpgradeModeOverride.Dial });
                }

                ChannelState state = new(upChannel, _windowSettings);
                // A peer may reply to SYN before the outbound pump finishes its first write.
                channels[streamId] = state;

                TaskCompletionSource channelClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                upChannel.GetAwaiter().OnCompleted(() => channelClosed.TrySetResult());
                long lastOutboundProgressTimestamp = _closedStreamIdleTimeout is null ? 0 : _timeProvider.GetTimestamp();
                bool outboundFinSent = false;

                Task outboundPump = Task.Run(async () =>
                {
                    try
                    {
                        await WriteHeaderAsync(contextId, channel,
                                   new YamuxHeader
                                   {
                                       Flags = initiationFlag,
                                       Type = YamuxHeaderType.WindowUpdate,
                                       StreamID = streamId,
                                       Length = state.LocalWindow.InitialWindowSize - ProtocolInitialWindowSize
                                   }, token: state.OutboundCancellation);

                        if (initiationFlag == YamuxHeaderFlags.Syn)
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: New stream request sent", contextId, streamId);
                        }
                        else
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: New stream request acknowledged", contextId, streamId);
                        }

                        await foreach (ReadOnlySequence<byte> upData in upChannel.ReadAllAsync())
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Receive from upchannel, length={length}", contextId, streamId, upData.Length);

                            for (int i = 0; i < upData.Length;)
                            {
                                int sendingSize = await state.RemoteWindow.SpendOrWait((int)upData.Length - i, state.OutboundCancellation);

                                _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Remote window spend {sendingSize}", contextId, streamId, sendingSize);

                                await WriteHeaderAsync(contextId, channel,
                                    new YamuxHeader
                                    {
                                        Type = YamuxHeaderType.Data,
                                        Length = sendingSize,
                                        StreamID = streamId
                                    }, new ReadOnlySequence<byte>(upData.Slice(i, sendingSize).ToArray()), state.OutboundCancellation);
                                if (_closedStreamIdleTimeout is not null)
                                    Volatile.Write(ref lastOutboundProgressTimestamp, _timeProvider.GetTimestamp());
                                i += sendingSize;
                            }
                        }

                        await WriteHeaderAsync(contextId, channel,
                            new YamuxHeader
                            {
                                Flags = YamuxHeaderFlags.Fin,
                                Type = YamuxHeaderType.WindowUpdate,
                                StreamID = streamId
                            }, token: state.OutboundCancellation);
                        outboundFinSent = true;
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Upchannel finished writing", contextId, streamId);
                    }
                    catch (ChannelClosedException) when (state.OutboundCancellation.IsCancellationRequested)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Outbound pump cancelled", contextId, streamId);
                    }
                    catch (ChannelClosedException e)
                    {
                        context.Activity?.AddEvent(new ActivityEvent($"exception {e.Message}"));
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Closed due to transport disconnection", contextId, streamId);
                    }
                    catch (OperationCanceledException) when (state.OutboundCancellation.IsCancellationRequested)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Outbound pump cancelled", contextId, streamId);
                    }
                    catch (Exception e)
                    {
                        using CancellationTokenSource resetTimeout = new(ControlWriteTimeout, _timeProvider);
                        try
                        {
                            await WriteHeaderAsync(contextId, channel,
                                new YamuxHeader
                                {
                                    Flags = YamuxHeaderFlags.Rst,
                                    Type = YamuxHeaderType.WindowUpdate,
                                    StreamID = streamId
                                }, token: resetTimeout.Token);
                        }
                        catch (ChannelClosedException) when (resetTimeout.IsCancellationRequested)
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Reset write timed out", contextId, streamId);
                        }
                        _ = upChannel.AbortAsync();
                        channels.TryRemove(streamId, out ChannelState? _);

                        if (e is ChannelAbortedException)
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Upchannel aborted, resetting", contextId, streamId);
                        }
                        else
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Unexpected error, closing: {error}", contextId, streamId, e.Message);
                        }
                    }
                });

                _ = CleanUpClosedStreamAsync();

                async Task ResetAbortedHalfClosedStreamAsync()
                {
                    // Cancellation can abort the stream after the outbound pump has already sent FIN.
                    if (!outboundFinSent || state.OutboundCancellation.IsCancellationRequested ||
                        (await upChannel.ReadAsync(0, ReadBlockingMode.DoNotWait)).Result != IOResult.Aborted)
                        return;

                    using CancellationTokenSource resetTimeout = new(ControlWriteTimeout, _timeProvider);
                    await WriteHeaderAsync(contextId, channel,
                        new YamuxHeader
                        {
                            Flags = YamuxHeaderFlags.Rst,
                            Type = YamuxHeaderType.WindowUpdate,
                            StreamID = streamId
                        }, token: resetTimeout.Token);
                }

                async Task CleanUpClosedStreamAsync()
                {
                    try
                    {
                        await channelClosed.Task;
                        if (_closedStreamIdleTimeout is not { } closedStreamIdleTimeout)
                        {
                            await outboundPump;
                            await ResetAbortedHalfClosedStreamAsync();
                            return;
                        }

                        TimeSpan remaining = closedStreamIdleTimeout;
                        while (!outboundPump.IsCompleted)
                        {
                            try
                            {
                                await outboundPump.WaitAsync(remaining, _timeProvider);
                            }
                            catch (TimeoutException) when (!outboundPump.IsCompleted)
                            {
                                remaining = closedStreamIdleTimeout -
                                    _timeProvider.GetElapsedTime(Volatile.Read(ref lastOutboundProgressTimestamp));
                                if (remaining > TimeSpan.Zero)
                                    continue;

                                _logger?.LogWarning("Ctx({ctx}), stream {stream id}: Closed stream made no outbound progress for {timeout}; resetting",
                                    contextId, streamId, closedStreamIdleTimeout);
                                state.AbortOutbound();
                                await outboundPump;
                                using CancellationTokenSource resetTimeout = new(ControlWriteTimeout, _timeProvider);
                                try
                                {
                                    await WriteHeaderAsync(contextId, channel,
                                        new YamuxHeader
                                        {
                                            Flags = YamuxHeaderFlags.Rst,
                                            Type = YamuxHeaderType.WindowUpdate,
                                            StreamID = streamId
                                        }, token: resetTimeout.Token);
                                }
                                catch (ChannelClosedException) when (resetTimeout.IsCancellationRequested)
                                {
                                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Reset write timed out", contextId, streamId);
                                }
                                break;
                            }
                        }
                        await outboundPump;
                        await ResetAbortedHalfClosedStreamAsync();
                    }
                    catch (Exception e)
                    {
                        _logger?.LogDebug(e, "Ctx({ctx}), stream {stream id}: Closed stream cleanup failed", contextId, streamId);
                    }
                    finally
                    {
                        channels.TryRemove(streamId, out ChannelState? _);
                        state.Dispose();
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Closed", contextId, streamId);
                    }
                }

                _logger?.LogDebug("Stream {stream id}: Create up channel, {mode}", streamId, isListenerChannel ? "listen" : "dial");
            }
        }
        catch (ChannelClosedException)
        {
            _logger?.LogDebug("Ctx({ctx}): Closed due to transport disconnection", context.Id);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Ctx({ctx}): Closed with exception \"{exception}\" {stackTrace}", context.Id, ex.Message, ex.StackTrace);
            await WriteGoAwayAsync(context.Id, channel, SessionTerminationCode.InternalError);
            await channel.CloseAsync();
        }
        finally
        {
            timer?.Dispose();
            session?.Dispose();

            foreach (ChannelState? upChannel in channels.Values)
            {
                context.Activity?.AddEvent(new ActivityEvent("close an up chan"));
                upChannel?.AbortOutbound();
                _ = upChannel?.Channel?.CloseAsync();
            }

            _ = channel.CloseAsync();
        }

        void ExtendWindow(IChannel channel, string sessionId, int streamId, IOResult result, int consumedBytes)
        {
            if (result == IOResult.Ok)
            {
                if (channels.TryGetValue(streamId, out ChannelState? channelState))
                {
                    channelState.LocalWindow.RecordConsumed(consumedBytes);
                    int extendedBy = channelState.LocalWindow.ExtendIfNeeded();
                    if (extendedBy is not 0)
                    {
                        _ = WriteHeaderAsync(sessionId, channel,
                            new YamuxHeader
                            {
                                Type = YamuxHeaderType.WindowUpdate,
                                Length = extendedBy,
                                StreamID = streamId
                            });
                    }
                }
            }
        }
    }

    private async Task<YamuxHeader> ReadHeaderAsync(string contextId, IReader reader, CancellationToken token = default)
    {
        byte[] headerData = (await reader.ReadAsync(HeaderLength, token: token).OrThrow()).ToArray();
        YamuxHeader header = YamuxHeader.FromBytes(headerData);
        _logger?.LogTrace("Ctx({ctx}), stream {stream id}: Receive type={type} flags={flags} length={length}", contextId, header.StreamID, header.Type, header.Flags, header.Length);
        return header;
    }

    private async Task WriteHeaderAsync(string contextId, IWriter writer, YamuxHeader header,
        ReadOnlySequence<byte> data = default, CancellationToken token = default)
    {
        byte[] headerBuffer = new byte[HeaderLength];
        if (header.Type == YamuxHeaderType.Data)
        {
            header.Length = (int)data.Length;
        }
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogTrace("Ctx({ ctx}), stream {stream id}: Send type={type} flags={flags} length={length}", contextId, header.StreamID, header.Type, header.Flags, header.Length);
        await writer.WriteAsync(data.Length == 0 ? new ReadOnlySequence<byte>(headerBuffer) : data.Prepend(headerBuffer), token).OrThrow();
    }

    private async Task WriteGoAwayAsync(string contextId, IWriter channel, SessionTerminationCode code)
    {
        // Best effort: the session is going down, so a dead transport must never
        // fail the farewell write and fault the session task.
        using CancellationTokenSource timeout = new(ControlWriteTimeout, _timeProvider);
        try
        {
            await WriteHeaderAsync(contextId, channel, new YamuxHeader
            {
                Type = YamuxHeaderType.GoAway,
                Length = (int)code,
                StreamID = 0,
            }, token: timeout.Token);
        }
        catch (ChannelClosedException)
        {
            _logger?.LogDebug("Ctx({ctx}): GoAway write failed, transport is gone", contextId);
        }
    }
}
