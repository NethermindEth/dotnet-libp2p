// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Libp2p.Protocols.Yamux.Tests")]

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol : SymmetricProtocol, IConnectionProtocol
{
    private const int HeaderLength = 12;
    private const int PingDelay = 30_000;

    public YamuxProtocol(MultiplexerSettings? multiplexerSettings = null, ILoggerFactory? loggerFactory = null)
    {
        multiplexerSettings?.Add(this);
        _logger = loggerFactory?.CreateLogger<YamuxProtocol>();
    }

    private readonly ILogger? _logger;

    public string Id => "/yamux/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IConnectionContext context, bool isListener)
    {
        _logger?.LogInformation("Ctx({ctx}): {mode} {peer}", context.Id, isListener ? "Listen" : "Dial", context.State.RemoteAddress);

        TaskAwaiter downChannelAwaiter = channel.GetAwaiter();
        Dictionary<int, ChannelState> channels = [];

        try
        {
            int streamIdCounter = isListener ? 2 : 1;
            using INewSessionContext session = context.UpgradeToSession();
            _logger?.LogInformation("Ctx({ctx}): Session created for {peer}", context.Id, context.State.RemoteAddress);
            int pingCounter = 0;

            using Timer timer = new((s) =>
            {
                _ = WriteHeaderAsync(context.Id, channel, new YamuxHeader { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = ++pingCounter });
            }, null, PingDelay, PingDelay);

            _ = Task.Run(() =>
            {
                foreach (UpgradeOptions request in session.DialRequests)
                {
                    int streamId = streamIdCounter;
                    Interlocked.Add(ref streamIdCounter, 2);

                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Dialing with protocol {proto}", context.Id, streamId, request.SelectedProtocol?.Id);
                    channels[streamId] = CreateUpchannel(context.Id, streamId, YamuxHeaderFlags.Syn, request);
                }
            });

            while (!downChannelAwaiter.IsCompleted)
            {
                YamuxHeader header = await ReadHeaderAsync(context.Id, channel);
                ReadOnlySequence<byte> data = default;

                if (header.Type > YamuxHeaderType.GoAway)
                {
                    // TODO: Handle bad packet
                }
                if (header.StreamID is 0)
                {
                    if (header.Type == YamuxHeaderType.Ping)
                    {
                        if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                        {
                            _ = WriteHeaderAsync(context.Id, channel,
                                new YamuxHeader
                                {
                                    Flags = YamuxHeaderFlags.Ack,
                                    Type = YamuxHeaderType.Ping,
                                    Length = header.Length,
                                });

                            _logger?.LogDebug("Ctx({ctx}): Ping received and acknowledged", context.Id);
                        }
                        continue;
                    }

                    if (header.Type == YamuxHeaderType.GoAway)
                    {
                        _logger?.LogDebug("Ctx({ctx}): Closing all streams", context.Id);

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

                if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn && !channels.ContainsKey(header.StreamID))
                {
                    channels[header.StreamID] = CreateUpchannel(context.Id, header.StreamID, YamuxHeaderFlags.Ack, new UpgradeOptions());
                }

                if (!channels.ContainsKey(header.StreamID))
                {
                    if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                    {
                        await channel.ReadAsync(header.Length);
                    }
                    _logger?.LogDebug("Ctx({ctx}): Stream {stream id}: Ignored for closed stream", context.Id, header.StreamID);
                    continue;
                }

                if (header is { Type: YamuxHeaderType.Data, Length: not 0 })
                {
                    if (header.Length > channels[header.StreamID].LocalWindow.Available)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Data length > windows size: {length} > {window size}", context.Id,
                           header.StreamID, header.Length, channels[header.StreamID].LocalWindow.Available);

                        await WriteGoAwayAsync(context.Id, channel, SessionTerminationCode.ProtocolError);
                        return;
                    }

                    data = await channel.ReadAsync(header.Length).OrThrow();

                    bool spent = channels[header.StreamID].LocalWindow.SpendWindow((int)data.Length);
                    if (!spent)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Window spent out of budget", context.Id, header.StreamID);
                        await WriteGoAwayAsync(context.Id, channel, SessionTerminationCode.InternalError);
                        return;
                    }

                    ValueTask<IOResult> writeTask = channels[header.StreamID].Channel!.WriteAsync(data);

                    if (writeTask.IsCompleted)
                    {
                        if (writeTask.Result == IOResult.Ok)
                        {
                            int extendedBy = channels[header.StreamID].LocalWindow.ExtendWindowIfNeeded();
                            if (extendedBy is not 0)
                            {
                                _ = WriteHeaderAsync(context.Id, channel,
                                    new YamuxHeader
                                    {
                                        Type = YamuxHeaderType.WindowUpdate,
                                        Length = extendedBy,
                                        StreamID = header.StreamID
                                    });
                            }
                        }
                    }
                    else
                    {
                        writeTask.GetAwaiter().OnCompleted(() =>
                        {
                            if (writeTask.Result == IOResult.Ok && channels.TryGetValue(header.StreamID, out ChannelState? channelState))
                            {
                                int extendedBy = channelState.LocalWindow.ExtendWindowIfNeeded();
                                if (extendedBy is not 0)
                                {
                                    _ = WriteHeaderAsync(context.Id, channel,
                                        new YamuxHeader
                                        {
                                            Type = YamuxHeaderType.WindowUpdate,
                                            Length = extendedBy,
                                            StreamID = header.StreamID
                                        });
                                }
                            }
                        });
                    }
                }

                if (header.Type == YamuxHeaderType.WindowUpdate && header.Length != 0)
                {
                    int oldSize = channels[header.StreamID].RemoteWindow.Available;
                    int newSize = channels[header.StreamID].RemoteWindow.ExtendWindow(header.Length);
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Window update requested: {old} => {new}", context.Id, header.StreamID, oldSize, newSize);
                }

                if ((header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
                {
                    if (!channels.TryGetValue(header.StreamID, out ChannelState state))
                    {
                        continue;
                    }

                    _ = state.Channel?.WriteEofAsync();
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Finish receiving", context.Id, header.StreamID);
                }

                if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst)
                {
                    _ = channels[header.StreamID].Channel?.CloseAsync();
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Reset", context.Id, header.StreamID);
                }
            }

            await WriteGoAwayAsync(context.Id, channel, SessionTerminationCode.Ok);

            ChannelState CreateUpchannel(string contextId, int streamId, YamuxHeaderFlags initiationFlag, UpgradeOptions upgradeOptions)
            {
                bool isListenerChannel = isListener ^ (streamId % 2 == 0);

                _logger?.LogDebug("Stream {stream id}: Create up channel, {mode}", streamId, isListenerChannel ? "listen" : "dial");
                IChannel upChannel;

                if (isListenerChannel)
                {
                    upChannel = session.Upgrade(upgradeOptions with { ModeOverride = UpgradeModeOverride.Listen });
                }
                else
                {
                    upChannel = session.Upgrade(upgradeOptions with { ModeOverride = UpgradeModeOverride.Dial });
                }

                ChannelState state = new(upChannel);

                upChannel.GetAwaiter().OnCompleted(() =>
                {
                    channels.Remove(streamId);
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

                        await foreach (var upData in upChannel.ReadAllAsync())
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Receive from upchannel, length={length}", contextId, streamId, upData.Length);

                            for (int i = 0; i < upData.Length;)
                            {
                                int sendingSize = await state.RemoteWindow.SpendWindowOrWait((int)upData.Length - i);

                                await WriteHeaderAsync(contextId, channel,
                                    new YamuxHeader
                                    {
                                        Type = YamuxHeaderType.Data,
                                        Length = sendingSize,
                                        StreamID = streamId
                                    }, upData.Slice(i, sendingSize));
                                i += sendingSize;
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
                        channels.Remove(streamId);

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

        foreach (ChannelState upChannel in channels.Values)
        {
            _ = upChannel.Channel?.CloseAsync();
        }
    }

    private async Task<YamuxHeader> ReadHeaderAsync(string contextId, IReader reader, CancellationToken token = default)
    {
        byte[] headerData = (await reader.ReadAsync(HeaderLength, token: token).OrThrow()).ToArray();
        YamuxHeader header = YamuxHeader.FromBytes(headerData);
        _logger?.LogTrace("Ctx({ctx}), stream {stream id}: Receive type={type} flags={flags} length={length}", contextId, header.StreamID, header.Type, header.Flags, header.Length);
        return header;
    }

    private async Task WriteHeaderAsync(string contextId, IWriter writer, YamuxHeader header, ReadOnlySequence<byte> data = default)
    {
        byte[] headerBuffer = new byte[HeaderLength];
        if (header.Type == YamuxHeaderType.Data)
        {
            header.Length = (int)data.Length;
        }
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogTrace("Ctx({ ctx}), stream {stream id}: Send type={type} flags={flags} length={length}", contextId, header.StreamID, header.Type, header.Flags, header.Length);
        await writer.WriteAsync(data.Length == 0 ? new ReadOnlySequence<byte>(headerBuffer) : data.Prepend(headerBuffer)).OrThrow();
    }

    private Task WriteGoAwayAsync(string contextId, IWriter channel, SessionTerminationCode code) =>
        WriteHeaderAsync(contextId, channel, new YamuxHeader
        {
            Type = YamuxHeaderType.GoAway,
            Length = (int)code,
            StreamID = 0,
        });

    private class ChannelState(IChannel? channel = default)
    {
        public IChannel? Channel { get; set; } = channel;
        //public ChannelRequest? Request { get; set; } = request;

        public DataWindow LocalWindow { get; } = new();
        public DataWindow RemoteWindow { get; } = new();
    }

    public class DataWindow(int defaultWindowSize = 256 * 1024)
    {
        private int _defaultWindowSize = defaultWindowSize;
        private int _available = defaultWindowSize;
        private int _requestedSize;
        private TaskCompletionSource<int>? _windowSizeTcs;
        public int Available { get => _available; }

        internal int ExtendWindowIfNeeded()
        {
            if (_available < _defaultWindowSize / 2)
            {
                return ExtendWindow(_defaultWindowSize);
            }

            return 0;
        }

        internal int ExtendWindow(int length)
        {
            if (length is 0)
            {
                return 0;
            }

            lock (this)
            {
                _available += length;
                if (_windowSizeTcs is not null)
                {
                    int availableSize = Math.Min(_requestedSize, _available);
                    _available -= availableSize;
                    _windowSizeTcs.SetResult(availableSize);
                }
                return _available;
            }
        }

        internal async Task<int> SpendWindowOrWait(int requestedSize)
        {
            if (requestedSize is 0)
            {
                return 0;
            }
            if (_windowSizeTcs is not null)
            {
                await _windowSizeTcs.Task;
            }

            TaskCompletionSource<int>? taskToWait;

            lock (this)
            {
                if (_available is 0)
                {
                    taskToWait = _windowSizeTcs = new();
                    _requestedSize = requestedSize;
                }
                else
                {
                    int availableSize = Math.Min(requestedSize, _available);
                    _available -= availableSize;
                    return availableSize;
                }
            }

            return await taskToWait.Task;
        }

        internal bool SpendWindow(int requestedSize)
        {
            int result = Interlocked.Add(ref _available, -requestedSize);
            return result >= 0;
        }
    }
}
