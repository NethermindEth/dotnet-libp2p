// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol : SymmetricProtocol, IProtocol
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

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context, bool isListener)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException("ChannelFactory should be available for a muxer", nameof(channelFactory));
        }

        _logger?.LogInformation(isListener ? "Listen" : "Dial");

        TaskAwaiter downChannelAwaiter = channel.GetAwaiter();
        Dictionary<int, ChannelState> channels = new();

        try
        {
            int streamIdCounter = isListener ? 2 : 1;
            context.Connected(context.RemotePeer);
            int pingCounter = 0;

            using Timer timer = new((s) =>
            {
                _ = WriteHeaderAsync(channel, new YamuxHeader { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = ++pingCounter });
            }, null, PingDelay, PingDelay);

            _ = Task.Run(() =>
            {
                foreach (IChannelRequest request in context.SubDialRequests.GetConsumingEnumerable())
                {
                    int streamId = streamIdCounter;
                    Interlocked.Add(ref streamIdCounter, 2);

                    _logger?.LogDebug("Stream {stream id}: Dialing with protocol {proto}", streamId, request.SubProtocol?.Id);
                    channels[streamId] = CreateUpchannel(streamId, YamuxHeaderFlags.Syn, request);
                }
            });

            while (!downChannelAwaiter.IsCompleted)
            {
                YamuxHeader header = await ReadHeaderAsync(channel);
                ReadOnlySequence<byte> data = default;

                if (header.StreamID is 0)
                {
                    if (header.Type == YamuxHeaderType.Ping)
                    {
                        if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                        {
                            _ = WriteHeaderAsync(channel,
                                new YamuxHeader
                                {
                                    Flags = YamuxHeaderFlags.Ack,
                                    Type = YamuxHeaderType.Ping,
                                    Length = header.Length,
                                });

                            _logger?.LogDebug("Ping received and acknowledged");
                        }
                    }

                    if (header.Type == YamuxHeaderType.GoAway)
                    {
                        _logger?.LogDebug("Closing all streams");

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
                    channels[header.StreamID] = CreateUpchannel(header.StreamID, YamuxHeaderFlags.Ack, null);
                }

                if (!channels.ContainsKey(header.StreamID))
                {
                    if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                    {
                        await channel.ReadAsync(header.Length);
                    }
                    _logger?.LogDebug("Stream {stream id}: Ignored for closed stream", header.StreamID);
                    continue;
                }

                if (header is { Type: YamuxHeaderType.Data, Length: not 0 })
                {
                    if (header.Length > channels[header.StreamID].WindowSize)
                    {
                        _logger?.LogDebug("Stream {stream id}: Data length > windows size: {length} > {window size}",
                           header.StreamID, header.Length, channels[header.StreamID].WindowSize);

                        await WriteGoAwayAsync(channel, SessionTerminationCode.ProtocolError);
                        return;
                    }

                    data = new ReadOnlySequence<byte>((await channel.ReadAsync(header.Length).OrThrow()).ToArray());

                    _logger?.LogDebug("Stream {stream id}: Send to upchannel, length={length}", header.StreamID, data.Length);
                    await channels[header.StreamID].Channel!.WriteAsync(data);
                }

                if (header.Type == YamuxHeaderType.WindowUpdate && header.Length != 0)
                {
                    int oldSize = channels[header.StreamID].WindowSize;
                    int newSize = oldSize + header.Length;
                    _logger?.LogDebug("Stream {stream id}: Window update requested: {old} => {new}", header.StreamID, oldSize, newSize);
                    channels[header.StreamID].WindowSize = newSize;
                }

                if ((header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
                {
                    if (!channels.TryGetValue(header.StreamID, out ChannelState state))
                    {
                        continue;
                    }

                    _ = state.Channel?.WriteEofAsync();
                    _logger?.LogDebug("Stream {stream id}: Finish receiving", header.StreamID);
                }

                if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst)
                {
                    _ = channels[header.StreamID].Channel?.CloseAsync();
                    _logger?.LogDebug("Stream {stream id}: Reset", header.StreamID);
                }
            }

            await WriteGoAwayAsync(channel, SessionTerminationCode.Ok);

            ChannelState CreateUpchannel(int streamId, YamuxHeaderFlags initiationFlag, IChannelRequest? channelRequest)
            {
                bool isListenerChannel = isListener ^ (streamId % 2 == 0);

                _logger?.LogDebug("Stream {stream id}: Create up channel, {mode}", streamId, isListenerChannel ? "listen" : "dial");
                IChannel upChannel;

                if (isListenerChannel)
                {
                    upChannel = channelFactory.SubListen(context);
                }
                else
                {
                    IPeerContext dialContext = context.Fork();
                    dialContext.SpecificProtocolRequest = channelRequest;
                    upChannel = channelFactory.SubDial(dialContext);
                }

                ChannelState state = new(upChannel, channelRequest);
                TaskCompletionSource? tcs = state.Request?.CompletionSource;

                upChannel.GetAwaiter().OnCompleted(() =>
                {
                    tcs?.SetResult();
                    channels.Remove(streamId);
                    _logger?.LogDebug("Stream {stream id}: Closed", streamId);
                });

                Task.Run(async () =>
                {
                    try
                    {
                        await WriteHeaderAsync(channel,
                                   new YamuxHeader
                                   {
                                       Flags = initiationFlag,
                                       Type = YamuxHeaderType.WindowUpdate,
                                       StreamID = streamId
                                   });

                        if (initiationFlag == YamuxHeaderFlags.Syn)
                        {
                            _logger?.LogDebug("Stream {stream id}: New stream request sent", streamId);
                        }
                        else
                        {
                            _logger?.LogDebug("Stream {stream id}: New stream request acknowledged", streamId);
                        }

                        await foreach (var upData in upChannel.ReadAllAsync())
                        {
                            _logger?.LogDebug("Stream {stream id}: Receive from upchannel, length={length}", streamId, upData.Length);

                            for (int i = 0; i < upData.Length;)
                            {
                                int sendingSize = Math.Min((int)upData.Length - i, state.WindowSize);
                                await WriteHeaderAsync(channel,
                                   new YamuxHeader
                                   {
                                       Type = YamuxHeaderType.Data,
                                       Length = sendingSize,
                                       StreamID = streamId
                                   }, upData.Slice(i, sendingSize));
                                i += sendingSize;
                            }
                        }

                        await WriteHeaderAsync(channel,
                            new YamuxHeader
                            {
                                Flags = YamuxHeaderFlags.Fin,
                                Type = YamuxHeaderType.WindowUpdate,
                                StreamID = streamId
                            });
                        _logger?.LogDebug("Stream {stream id}: Upchannel finished writing", streamId);
                    }
                    catch (ChannelClosedException e)
                    {
                        _logger?.LogDebug("Stream {stream id}: Closed due to transport disconnection", streamId);
                    }
                    catch (Exception e)
                    {
                        await WriteHeaderAsync(channel,
                          new YamuxHeader
                          {
                              Flags = YamuxHeaderFlags.Rst,
                              Type = YamuxHeaderType.WindowUpdate,
                              StreamID = streamId
                          });
                        _ = upChannel.CloseAsync();
                        channels.Remove(streamId);

                        _logger?.LogDebug("Stream {stream id}: Unexpected error, closing: {error}", streamId, e.Message);
                    }
                });

                return state;
            }
        }
        catch (ChannelClosedException ex)
        {
            _logger?.LogDebug("Closed due to transport disconnection");
        }
        catch (Exception ex)
        {
            await WriteGoAwayAsync(channel, SessionTerminationCode.InternalError);
            _logger?.LogDebug("Closed with exception {exception}", ex.Message);
            _logger?.LogTrace("{stackTrace}", ex.StackTrace);
        }

        foreach (ChannelState upChannel in channels.Values)
        {
            _ = upChannel.Channel?.CloseAsync();
        }
    }

    private async Task<YamuxHeader> ReadHeaderAsync(IReader reader, CancellationToken token = default)
    {
        byte[] headerData = (await reader.ReadAsync(HeaderLength, token: token).OrThrow()).ToArray();
        YamuxHeader header = YamuxHeader.FromBytes(headerData);
        _logger?.LogTrace("Stream {stream id}: Receive type={type} flags={flags} length={length}", header.StreamID, header.Type, header.Flags, header.Length);
        return header;
    }

    private async Task WriteHeaderAsync(IWriter writer, YamuxHeader header, ReadOnlySequence<byte> data = default)
    {
        byte[] headerBuffer = new byte[HeaderLength];
        if (header.Type == YamuxHeaderType.Data)
        {
            header.Length = (int)data.Length;
        }
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogTrace("Stream {stream id}: Send type={type} flags={flags} length={length}", header.StreamID, header.Type, header.Flags, header.Length);
        await writer.WriteAsync(data.Length == 0 ? new ReadOnlySequence<byte>(headerBuffer) : data.Prepend(headerBuffer)).OrThrow();
    }

    private Task WriteGoAwayAsync(IWriter channel, SessionTerminationCode code) =>
        WriteHeaderAsync(channel, new YamuxHeader
        {
            Type = YamuxHeaderType.GoAway,
            Length = (int)code,
            StreamID = 0,
        });

    private class ChannelState(IChannel? channel = default, IChannelRequest? request = default)
    {
        public IChannel? Channel { get; set; } = channel;
        public IChannelRequest? Request { get; set; } = request;
        public int WindowSize { get; set; } = 256 * 1024;
    }
}
