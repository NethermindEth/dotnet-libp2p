// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Threading.Channels;
using System.Xml.Linq;

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol(ILoggerFactory? loggerFactory = null) : SymmetricProtocol, IProtocol
{
    private const int HeaderLength = 12;
    private const int PingDelay = 30_000;

    private readonly ILogger? _logger = loggerFactory?.CreateLogger<YamuxProtocol>();

    public string Id => "/yamux/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context, bool isListener)
    {
        try
        {
            if (channelFactory is null)
            {
                throw new ArgumentException("ChannelFactory should be available for a muxer", nameof(channelFactory));
            }

            _logger?.LogInformation(isListener ? "Listen" : "Dial");
            int streamIdCounter = isListener ? 2 : 1;

            Dictionary<int, ChannelState> channels = new();

            context.Connected(context.RemotePeer);

            int pingCounter = 0;

            using Timer timer = new((s) =>
            {
                int localPingCounter = ++pingCounter;
                _ = WriteHeaderAsync(channel, new YamuxHeader { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = localPingCounter });
            }, null, 0, PingDelay);

            _ = Task.Run(() =>
            {
                foreach (IChannelRequest request in context.SubDialRequests.GetConsumingEnumerable())
                {
                    int streamId = streamIdCounter;
                    Interlocked.Add(ref streamIdCounter, 2);

                    _logger?.LogDebug("Stream {stream id}: Dialing with protocol {proto}", streamId, request.SubProtocol?.Id);
                    channels[streamId] = new ChannelState { Request = request };

                    _ = ActivateUpchannel(streamId, YamuxHeaderFlags.Syn, request);
                }
            });

            for (; ; )
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

                if (channels.TryAdd(header.StreamID, new()) || (header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                {
                    _logger?.LogDebug("Stream {stream id}: Requested", header.StreamID);

                    _ = WriteHeaderAsync(channel,
                        new YamuxHeader
                        {
                            Flags = YamuxHeaderFlags.Ack,
                            Type = YamuxHeaderType.Data,
                            StreamID = header.StreamID
                        });
                }

                if (header is { Type: YamuxHeaderType.Data, Length: not 0 })
                {
                    if (header.Length > channels[header.StreamID].WindowSize)
                    {
                        _logger?.LogDebug("Stream {stream id}: data length > windows size: {length} > {window size}",
                           header.StreamID, data.Length, channels[header.StreamID].WindowSize);

                        await WriteGoAwayAsync(channel, SessionTerminationCode.ProtocolError);
                        return;
                    }

                    data = new ReadOnlySequence<byte>((await channel.ReadAsync(header.Length).OrThrow()).ToArray());
                    _logger?.LogDebug("Stream {stream id}: Read {1}", header.StreamID, data.Length);
                }

                if (channels[header.StreamID].Channel is null)
                {
                    _ = ActivateUpchannel(header.StreamID, YamuxHeaderFlags.Ack, null);
                    _logger?.LogDebug("Stream {stream id}: Acknowledged", header.StreamID);
                }

                if (header.Type == YamuxHeaderType.Data)
                {
                    _logger?.LogDebug("Stream {stream id}: Send to upchannel, length={length}", header.StreamID, data.Length);
                    await channels[header.StreamID].Channel!.WriteAsync(data);
                }

                if (header.Type == YamuxHeaderType.WindowUpdate)
                {
                    if (header.Length != 0)
                    {
                        _logger?.LogDebug("Stream {stream id}: Window update requested: {old} => {new}", header.StreamID, channels[header.StreamID].WindowSize, header.Length);
                        channels[header.StreamID] = channels[header.StreamID] with { WindowSize = header.Length };
                    }
                    else
                    {
                        _logger?.LogDebug("Stream {stream id}: Window update as a signal received: {flags}", header.StreamID, header.Flags);
                    }
                }

                if ((header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
                {
                    if (!channels.TryGetValue(header.StreamID, out ChannelState state))
                    {
                        continue;
                    }

                    IChannel? upChannel = state.Channel;
                    if (upChannel is null)
                    {
                        continue;
                    }

                    _ = upChannel.WriteEofAsync();
                    _logger?.LogDebug("Stream {stream id}: Finish receiving", header.StreamID);

                    if (upChannel.GetAwaiter().IsCompleted)
                    {
                        channels.Remove(header.StreamID);
                        _logger?.LogDebug("Stream {stream id}: Closed", header.StreamID);
                    }
                }

                if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst)
                {
                    _ = channels[header.StreamID].Channel?.CloseAsync();
                    channels.Remove(header.StreamID);
                    _logger?.LogDebug("Stream {stream id}: Reset", header.StreamID);
                }
            }

            await WriteGoAwayAsync(channel, SessionTerminationCode.Ok);

            async Task ActivateUpchannel(int streamId, YamuxHeaderFlags initiationFlag, IChannelRequest? channelRequest)
            {
                if (channels[streamId].Channel is not null)
                {
                    return;
                }

                bool isListenerChannel = isListener ^ (streamId % 2 == 0);

                _logger?.LogDebug("Create chan for Stream {stream id} isListener = {1}", streamId, isListenerChannel);
                IChannel upChannel;

                if (isListenerChannel)
                {
                    upChannel = channelFactory.SubListen(context);
                }
                else
                {
                    IPeerContext dialContext = context.Fork();
                    dialContext.SpecificProtocolRequest = channels[streamId].Request;
                    upChannel = channelFactory.SubDial(dialContext);
                }

                channels[streamId] = new(upChannel, channelRequest);
                TaskCompletionSource? tcs = channels[streamId].Request?.CompletionSource;

                try
                {
                    await WriteHeaderAsync(channel,
                               new YamuxHeader
                               {
                                   Flags = initiationFlag,
                                   Type = YamuxHeaderType.Data,
                                   StreamID = streamId
                               });

                    await foreach (var upData in upChannel.ReadAllAsync())
                    {
                        _logger?.LogDebug("Stream {stream id}: Receive from upchannel, length={length}", streamId, upData.Length);

                        for (int i = 0; i < upData.Length;)
                        {
                            int sendingSize = Math.Min((int)upData.Length - i, channels[streamId].WindowSize);
                            await WriteHeaderAsync(channel,
                               new YamuxHeader
                               {
                                   Type = YamuxHeaderType.Data,
                                   Length = (int)upData.Length,
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

                    await upChannel;
                    channels.Remove(streamId);

                    _logger?.LogDebug("Stream {stream id}: Closed", streamId);
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
                finally
                {
                    tcs?.SetResult();
                }
            }
        }
        catch (Exception ex)
        {
            await WriteGoAwayAsync(channel, SessionTerminationCode.InternalError);
            _logger?.LogDebug("Closed with exception {exception}", ex.Message);
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
        header.Length = (int)data.Length;
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogTrace("Stream {stream id}: Send type={type} flags={flags} length={length}", header.StreamID, header.Type, header.Flags, header.Length);
        await writer.WriteAsync(
            data.Length == 0 ? new ReadOnlySequence<byte>(headerBuffer) : data.Prepend(headerBuffer)).OrThrow();
    }

    private Task WriteGoAwayAsync(IWriter channel, SessionTerminationCode code) =>
        WriteHeaderAsync(channel, new YamuxHeader
        {
            Type = YamuxHeaderType.GoAway,
            Length = (int)code,
            StreamID = 0,
        });

    private struct ChannelState(IChannel? channel, IChannelRequest? request = default)
    {
        public IChannel? Channel { get; set; } = channel;
        public IChannelRequest? Request { get; set; } = request;
        public int WindowSize { get; set; } = 256_000;
    }
}
