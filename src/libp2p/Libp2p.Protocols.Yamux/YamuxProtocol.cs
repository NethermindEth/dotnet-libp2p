// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol : SymmetricProtocol, IProtocol
{
    private readonly ILogger? _logger;

    public YamuxProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<YamuxProtocol>();
    }

    public string Id => "/yamux/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context, bool isListener)
    {
        if (channelFactory is null)
        {
            throw new ArgumentException("ChannelFactory should be available for a muxer", nameof(channelFactory));
        }
        _logger?.LogInformation("connect yamux");
        int streamIdCounter = isListener ? 2 : 1;
        Dictionary<int, ChannelState> channels = new()
        {
            [0] = new ChannelState { State = 1 }
        };
        await WriteHeaderAsync(channel,
            new YamuxHeader { Flags = YamuxHeaderFlags.Syn, Type = YamuxHeaderType.Ping, StreamID = 0 });

        await Task.Run(async () =>
        {
            while (!channel.IsClosed)
            {
                YamuxHeader header = await ReadHeaderAsync(channel);
                ReadOnlySequence<byte> data = default;
                if (header is { Type: YamuxHeaderType.Data })
                {
                    data = await channel.ReadAsync(header.Length);
                    _logger?.LogDebug("Recv data, stream-{0}, len={1}", header.StreamID, data.Length);
                }

                if (header.StreamID == 0)
                {
                    if (header.Flags == YamuxHeaderFlags.Syn)
                    {
                        await WriteHeaderAsync(channel,
                            new YamuxHeader
                            { Flags = YamuxHeaderFlags.Ack, Type = YamuxHeaderType.Ping, StreamID = 0 });
                    }

                    if (header.Flags == YamuxHeaderFlags.Ack)
                    {
                        channels[header.StreamID].State = 2;
                        context.Connected(context.RemotePeer);
                        _ = Task.Run(() =>
                        {

                            foreach (IChannelRequest request in context.SubDialRequests
                                         .GetConsumingEnumerable())
                            {
                                int streamId = streamIdCounter += 2;
                                _logger?.LogDebug("Trying to dial with proto {proto} via stream-{streamId}", request.SubProtocol?.Id, streamId);
                                channels[streamId] = new ChannelState { State = 1, Request = request };
                                _ = WriteHeaderAsync(channel,
                                    new YamuxHeader
                                    {
                                        Flags = YamuxHeaderFlags.Syn,
                                        Type = YamuxHeaderType.WindowUpdate,
                                        StreamID = streamId
                                    });
                            }
                        });
                    }

                    continue;
                }

                channels.TryAdd(header.StreamID, new ChannelState { State = 0 });

                if (channels[header.StreamID].State == 0 && header.Flags == YamuxHeaderFlags.Syn)
                {
                    channels[header.StreamID].State = 2;
                    _ = WriteHeaderAsync(channel,
                        new YamuxHeader
                        {
                            Flags = YamuxHeaderFlags.Ack,
                            Type = YamuxHeaderType.WindowUpdate,
                            StreamID = header.StreamID
                        });
                    _logger?.LogDebug("Request for a stream");

                }
                else if (channels[header.StreamID].State == 1 && header.Flags == YamuxHeaderFlags.Ack)
                {
                    channels[header.StreamID].State = 2;
                }

                if (channels[header.StreamID].State == 2)
                {
                    if (channels[header.StreamID].Channel is null)
                    {
                        bool isListenerChannel = isListener ^ (header.StreamID % 2 == 0);
                        int streamId = header.StreamID;
                        _logger?.LogDebug("Create chan for stream-{0} isListener = {1}", streamId, isListenerChannel);
                        IChannel upChannel;

                        if (isListenerChannel)
                        {
                            upChannel = channelFactory.SubListen(context);
                        }
                        else
                        {
                            IPeerContext dialContext = context.Fork();
                            dialContext.SpecificProtocolRequest = channels[header.StreamID].Request;
                            upChannel = channelFactory.SubDial(dialContext);
                        }

                        upChannel.OnClose(async () =>
                        {
                            await WriteHeaderAsync(channel,
                                new YamuxHeader
                                {
                                    Flags = YamuxHeaderFlags.Fin,
                                    Type = YamuxHeaderType.Data,
                                    StreamID = streamId
                                });
                            channels[header.StreamID].Request?.CompletionSource?.SetResult();
                            _logger?.LogDebug("Close, stream-{0}", streamId);
                        });
                        channels[header.StreamID].Channel = upChannel;
                        _ = Task.Run(async () =>
                        {
                            while (!channel.IsClosed)
                            {
                                ReadOnlySequence<byte> upData =
                                    await channels[streamId].Channel!.ReadAsync(0, ReadBlockingMode.WaitAny,
                                        channel.Token);
                                await WriteHeaderAsync(channel,
                                    new YamuxHeader
                                    {
                                        Type = YamuxHeaderType.Data,
                                        Length = (int)upData.Length,
                                        StreamID = streamId
                                    }, upData);
                            }
                        });
                    }

                    if (header.Type == YamuxHeaderType.Data)
                    {
                        _logger?.LogDebug("Data, stream-{0}, len={1}", header.StreamID, data.Length);
                        await channels[header.StreamID].Channel!.WriteAsync(data);
                    }

                    if (header.Flags == YamuxHeaderFlags.Fin)
                    {
                        _logger?.LogDebug("Fin, stream-{0}", header.StreamID);
                    }

                    if (header.Flags == YamuxHeaderFlags.Rst)
                    {
                        _logger?.LogDebug("Rst, stream-{0}", header.StreamID);
                    }
                }
            }
        });
    }

    private async Task<YamuxHeader> ReadHeaderAsync(IReader reader)
    {
        byte[] headerData = (await reader.ReadAsync(12)).ToArray();
        YamuxHeader header = YamuxHeader.FromBytes(headerData);
        _logger?.LogDebug("Read header, stream-{0} type={1} flags={2}{3}", header.StreamID, header.Type, header.Flags,
            header.Type == YamuxHeaderType.Data ? $", {header.Length}B content" : ""
            );
        return header;
    }

    private async Task WriteHeaderAsync(IWriter writer, YamuxHeader header, ReadOnlySequence<byte> data = default)
    {
        byte[] headerBuffer = new byte[12];
        header.Length = (int)data.Length;
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogDebug("Write header, stream-{0} type={1} flags={2}{3}", header.StreamID, header.Type, header.Flags,
            header.Type != YamuxHeaderType.Data ? "" : " data: " +
            Encoding.ASCII.GetString(data.ToArray().Select(c => c == 0x1b || c == 0x07 ? (byte)0x2e : c).ToArray()));
        await writer.WriteAsync(
            data.Length == 0 ? new ReadOnlySequence<byte>(headerBuffer) : data.Prepend(headerBuffer));
    }

    private class ChannelState
    {
        public int State { get; set; }
        public IChannel? Channel { get; set; }
        public IChannelRequest? Request { get; set; }
    }
}
