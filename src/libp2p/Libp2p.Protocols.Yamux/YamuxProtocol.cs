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

    private const string NoSession = "pending";
    public YamuxProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null, YamuxWindowSettings? windowSettings = null)
    {
        multiplexerSettings?.Add(this);
        _logger = loggerFactory?.CreateLogger<YamuxProtocol>();
        _windowSettings = windowSettings ?? new YamuxWindowSettings();
    }

    private readonly ILogger? _logger;
    private readonly YamuxWindowSettings _windowSettings;

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
                    Observe(WriteHeaderAsync(session.Id, channel, new YamuxHeader { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = (int)(++pingCounter % int.MaxValue) }));
                }, null, PingDelay, PingDelay);

                foreach (UpgradeOptions request in session.DialRequests)
                {
                    int streamId = streamIdCounter;
                    Interlocked.Add(ref streamIdCounter, 2);

                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Dialing with protocol {proto}", session.Id, streamId, request.SelectedProtocol?.Id);
                    channels[streamId] = CreateUpchannel(session.Id, streamId, YamuxHeaderFlags.Syn, request);
                }
            });

            while (!downChannelAwaiter.IsCompleted)
            {
                YamuxHeader header = await ReadHeaderAsync(session?.Id ?? NoSession, channel, channel.CancellationToken);
                if (header.Type > YamuxHeaderType.GoAway)
                {
                    _logger?.LogWarning("Ctx({ctx}): Bad packet received, type: {}", session?.Id ?? NoSession, header.Type);
                    await WriteGoAwayAsync(session?.Id ?? NoSession, channel, SessionTerminationCode.ProtocolError);
                    return;
                }

                if (header.StreamID is 0)
                {
                    if (header.Type == YamuxHeaderType.Ping)
                    {
                        if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                        {
                            await WriteHeaderAsync(session?.Id ?? NoSession, channel,
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
                    channels[header.StreamID] = CreateUpchannel(session.Id, header.StreamID, YamuxHeaderFlags.Ack, new UpgradeOptions());
                }

                if (!channels.ContainsKey(header.StreamID))
                {
                    if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                    {
                        using ReadResult ignored = await channel.ReadAsync(header.Length).OrThrow();
                    }
                    _logger?.LogDebug("Ctx({ctx}): Stream {stream id}: Ignored for closed stream", session.Id, header.StreamID);
                    continue;
                }

                if (header is { Type: YamuxHeaderType.Data, Length: not 0 })
                {
                    int available = channels[header.StreamID].LocalWindow.Available;
                    if (header.Length > available)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Data length > windows size: {length} > {window size}", session.Id,
                           header.StreamID, header.Length, channels[header.StreamID].LocalWindow.Available);

                        await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.ProtocolError);
                        return;
                    }

                    using ReadResult data = await channel.ReadAsync(header.Length).OrThrow();

                    bool spent = channels[header.StreamID].LocalWindow.TrySpend(data.Length);
                    if (!spent)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Local window spent out of budget", session.Id, header.StreamID);
                        await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.InternalError);
                        return;
                    }

                    if (_logger is { } logger && logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("Ctx({ctx}), stream {stream id}: Local spent window, was {available}, became {new}", session.Id,
                            header.StreamID, available, channels[header.StreamID].LocalWindow.Available);
                    }

                    int dataLength = data.Length;
                    using PooledBuffer.Slice dataSlice = data.ToSlice();
                    IOResult result = await channels[header.StreamID].Channel!.WriteAsync(dataSlice);
                    if (result != IOResult.Ok)
                    {
                        _logger?.LogWarning("Ctx({ctx}), stream {stream id}: Failed to send upstream", session.Id, header.StreamID);
                    }

                    await ExtendWindowAsync(channel, session.Id, header.StreamID, result, dataLength);
                }

                if (header.Type == YamuxHeaderType.WindowUpdate && header.Length != 0)
                {
                    int oldSize = channels[header.StreamID].RemoteWindow.Available;
                    int newSize = channels[header.StreamID].RemoteWindow.Extend(header.Length);
                    if (_logger is { } logger && logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("Ctx({ctx}), stream {stream id}: Window update received: {old} => {new}", session.Id, header.StreamID, oldSize, newSize);
                    }
                }

                if ((header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
                {
                    if (!channels.TryGetValue(header.StreamID, out ChannelState? state))
                    {
                        continue;
                    }

                    _ = state.Channel?.WriteEofAsync();
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Finish receiving", session.Id, header.StreamID);
                }

                if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst)
                {
                    _ = channels[header.StreamID].Channel?.CloseAsync();
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Reset", session.Id, header.StreamID);
                }
            }

            await WriteGoAwayAsync(session?.Id ?? NoSession, channel, SessionTerminationCode.Ok);

            ChannelState CreateUpchannel(string contextId, int streamId, YamuxHeaderFlags initiationFlag, UpgradeOptions upgradeOptions)
            {
                bool isListenerChannel = isListener ^ (streamId % 2 == 0);

                _logger?.LogDebug("Stream {stream id}: Create up channel, {mode}", streamId, isListenerChannel ? "listen" : "dial");
                IChannel upChannel;
                UpgradeOptions channelOptions = upgradeOptions with
                {
                    BufferHints = new ChannelBufferHints(HeaderLength)
                };

                if (isListenerChannel)
                {
                    upChannel = session.Upgrade(channelOptions with { ModeOverride = UpgradeModeOverride.Listen });
                }
                else
                {
                    upChannel = session.Upgrade(channelOptions with { ModeOverride = UpgradeModeOverride.Dial });
                }

                ChannelState state = new(upChannel, _windowSettings);

                upChannel.GetAwaiter().OnCompleted(() =>
                {
                    channels.TryRemove(streamId, out ChannelState? _);
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Closed", contextId, streamId);
                });

                Task.Run(async () =>
                {
                    try
                    {
                        await WriteHeaderAsync(contextId, channel,
                                   new YamuxHeader
                                   {
                                       Flags = initiationFlag,
                                       Type = YamuxHeaderType.WindowUpdate,
                                       StreamID = streamId
                                   });

                        if (initiationFlag == YamuxHeaderFlags.Syn)
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: New stream request sent", contextId, streamId);
                        }
                        else
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: New stream request acknowledged", contextId, streamId);
                        }

                        await foreach (PooledBuffer.Slice upData in upChannel.ReadAllAsync())
                        {
                            try
                            {
                                if (_logger is { } readLogger && readLogger.IsEnabled(LogLevel.Debug))
                                {
                                    readLogger.LogDebug("Ctx({ctx}), stream {stream id}: Receive from upchannel, length={length}", contextId, streamId, upData.Length);
                                }

                                for (int i = 0; i < upData.Length;)
                                {
                                    int sendingSize = await state.RemoteWindow.SpendOrWait(upData.Length - i, state.Channel!.CancellationToken);

                                    if (_logger is { } spendLogger && spendLogger.IsEnabled(LogLevel.Debug))
                                    {
                                        spendLogger.LogDebug("Ctx({ctx}), stream {stream id}: Remote window spend {sendingSize}", contextId, streamId, sendingSize);
                                    }

                                    using PooledBuffer.Slice frameData = upData.SliceRange(i, sendingSize);
                                    await WriteHeaderAsync(contextId, channel,
                                        new YamuxHeader
                                        {
                                            Type = YamuxHeaderType.Data,
                                            Length = sendingSize,
                                            StreamID = streamId
                                        }, frameData);
                                    i += sendingSize;
                                }
                            }
                            finally
                            {
                                upData.Dispose();
                            }
                        }

                        await WriteHeaderAsync(contextId, channel,
                            new YamuxHeader
                            {
                                Flags = YamuxHeaderFlags.Fin,
                                Type = YamuxHeaderType.WindowUpdate,
                                StreamID = streamId
                            });
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Upchannel finished writing", contextId, streamId);
                    }
                    catch (ChannelClosedException e)
                    {
                        context.Activity?.AddEvent(new ActivityEvent($"exception {e.Message}"));
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Closed due to transport disconnection", contextId, streamId);
                    }
                    catch (Exception e)
                    {
                        await WriteHeaderAsync(contextId, channel,
                          new YamuxHeader
                          {
                              Flags = YamuxHeaderFlags.Rst,
                              Type = YamuxHeaderType.WindowUpdate,
                              StreamID = streamId
                          });
                        _ = upChannel.CloseAsync();
                        channels.TryRemove(streamId, out ChannelState? _);

                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Unexpected error, closing: {error}", contextId, streamId, e.Message);
                    }
                });

                return state;
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
                _ = upChannel?.Channel?.CloseAsync();
            }

            _ = channel.CloseAsync();
        }

        async ValueTask ExtendWindowAsync(IChannel channel, string sessionId, int streamId, IOResult result, int consumedBytes)
        {
            if (result == IOResult.Ok)
            {
                if (channels.TryGetValue(streamId, out ChannelState? channelState))
                {
                    channelState.LocalWindow.RecordConsumed(consumedBytes);
                    int extendedBy = channelState.LocalWindow.ExtendIfNeeded();
                    if (extendedBy is not 0)
                    {
                        await WriteHeaderAsync(sessionId, channel,
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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<YamuxHeader> ReadHeaderAsync(string contextId, IReader reader, CancellationToken token = default)
    {
        using ReadResult headerData = await reader.ReadAsync(HeaderLength, token: token).OrThrow();
        YamuxHeader header = YamuxHeader.FromBytes(headerData.Data);
        if (_logger is { } logger && logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Ctx({ctx}), stream {stream id}: Receive type={type} flags={flags} length={length}", contextId, header.StreamID, header.Type, header.Flags, header.Length);
        }
        return header;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteHeaderAsync(string contextId, IWriter writer, YamuxHeader header, PooledBuffer.Slice data = default)
    {
        if (header.Type == YamuxHeaderType.Data)
        {
            header.Length = data.Length;
        }

        if (_logger is { } logger && logger.IsEnabled(LogLevel.Trace))
        {
            logger.LogTrace("Ctx({ ctx}), stream {stream id}: Send type={type} flags={flags} length={length}", contextId, header.StreamID, header.Type, header.Flags, header.Length);
        }

        if (data.Length != 0 && data.TryPrepend(HeaderLength, out PooledBuffer.Slice frame))
        {
            using (frame)
            {
                YamuxHeader.ToBytes(frame.Span[..HeaderLength], ref header);
                await writer.WriteAsync(frame).OrThrow();
                return;
            }
        }

        if (data.Length != 0 && header.Type == YamuxHeaderType.Data && _windowSettings.RequireDataFrameHeadroom)
        {
            throw new InvalidOperationException("Yamux data frames require payload buffers with reserved header headroom.");
        }

        using PooledBuffer headerBuffer = PooledBuffer.Rent(HeaderLength);
        YamuxHeader.ToBytes(headerBuffer.Span, ref header);

        if (data.Length == 0)
        {
            await writer.WriteAsync(headerBuffer, HeaderLength).OrThrow();
            return;
        }

        using PooledBuffer.Slice headerSlice = headerBuffer[0..HeaderLength];
        PooledBuffer.Slice[] slices = ArrayPool<PooledBuffer.Slice>.Shared.Rent(2);
        slices[0] = headerSlice;
        slices[1] = data;
        ValueTask<IOResult> write;
        try
        {
            write = writer.WriteAsync(slices.AsSpan(0, 2));
        }
        finally
        {
            ArrayPool<PooledBuffer.Slice>.Shared.Return(slices, clearArray: true);
        }

        await write.OrThrow();
    }

    private ValueTask WriteGoAwayAsync(string contextId, IWriter channel, SessionTerminationCode code) =>
        WriteHeaderAsync(contextId, channel, new YamuxHeader
        {
            Type = YamuxHeaderType.GoAway,
            Length = (int)code,
            StreamID = 0,
        });

    private void Observe(ValueTask operation)
    {
        if (operation.IsCompleted)
        {
            try
            {
                operation.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("Yamux background header write failed: {error}", ex.Message);
            }

            return;
        }

        _ = ObserveAsync(operation);
    }

    private async Task ObserveAsync(ValueTask operation)
    {
        try
        {
            await operation;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Yamux background header write failed: {error}", ex.Message);
        }
    }
}
