// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol : SymetricProtocol, IProtocol
{
    private readonly ILogger? _logger;

    public YamuxProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<YamuxProtocol>();
    }

    public string Id => "/yamux/1.0.0";

    protected override async Task ConnectAsync(IChannel channel, IChannelFactory channelFactory,
        IPeerContext context, bool isListener)
    {
        int streamIdCounter = isListener ? 2 : 1;
        Dictionary<int, ChannelState> channels = new()
        {
            [0] = new ChannelState { State = 1 }
        };
        await WriteHeaderAsync(channel.Writer,
            new YamuxHeader { Flags = YamuxHeaderFlags.Syn, Type = YamuxHeaderType.Ping, StreamID = 0 });

        _ = Task.Run(async () =>
        {
            while (true)
            {
                YamuxHeader header = await ReadHeaderAsync(channel.Reader);
                ReadOnlySequence<byte> data = default;
                if (header is { Type: YamuxHeaderType.Data, Flags: 0 })
                {
                    data = await channel.Reader.ReadAsync(header.Length);
                }

                if (header.StreamID == 0)
                {
                    if (header.Flags == YamuxHeaderFlags.Syn)
                    {
                        await WriteHeaderAsync(channel.Writer,
                            new YamuxHeader
                                { Flags = YamuxHeaderFlags.Ack, Type = YamuxHeaderType.Ping, StreamID = 0 });
                    }

                    if (header.Flags == YamuxHeaderFlags.Ack)
                    {
                        channels[header.StreamID].State = 2;

                        _ = Task.Run(async () =>
                        {
                            channelFactory.Connected(context.RemotePeer as IRemotePeer);
                            foreach (IChannelRequest request in channelFactory.SubDialRequests
                                         .GetConsumingEnumerable())
                            {
                                streamIdCounter += 2;
                                channels[streamIdCounter] = new ChannelState { State = 1, Request = request };
                                _ = WriteHeaderAsync(channel.Writer,
                                    new YamuxHeader
                                    {
                                        Flags = YamuxHeaderFlags.Syn, Type = YamuxHeaderType.WindowUpdate,
                                        StreamID = streamIdCounter
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
                    await WriteHeaderAsync(channel.Writer,
                        new YamuxHeader
                        {
                            Flags = YamuxHeaderFlags.Ack, Type = YamuxHeaderType.WindowUpdate,
                            StreamID = header.StreamID
                        });
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
                        IChannel chan = isListenerChannel
                            ? channelFactory.SubListen(context)
                            : channelFactory.SubDial(context, channels[header.StreamID].Request);

                        chan.OnClose(async () =>
                        {
                            if (!isListenerChannel)
                            {
                                await WriteHeaderAsync(channel.Writer,
                                    new YamuxHeader
                                    {
                                        Flags = YamuxHeaderFlags.Fin, Type = YamuxHeaderType.WindowUpdate,
                                        StreamID = streamId
                                    });
                            }

                            _logger?.LogDebug("Close, stream-{0}", streamId);
                        });
                        channels[header.StreamID].Channel = chan;
                        _ = Task.Run(async () =>
                        {
                            while (!channel.IsClosed)
                            {
                                ReadOnlySequence<byte> upData =
                                    await channels[streamId].Channel!.Reader.ReadAsync(0, ReadBlockingMode.WaitAny,
                                        channel.Token);
                                await WriteHeaderAsync(channel.Writer,
                                    new YamuxHeader
                                    {
                                        Type = YamuxHeaderType.Data, Length = (int)upData.Length, StreamID = streamId
                                    }, upData);
                            }
                        });
                    }

                    if (header.Type == YamuxHeaderType.Data && header.Flags == 0)
                    {
                        await channels[header.StreamID].Channel!.Writer.WriteAsync(data);
                        _logger?.LogDebug("Data, stream-{0}, len={1}", header.StreamID, data.Length);
                    }

                    if (header.Flags == YamuxHeaderFlags.Fin)
                    {
                    }
                }
            }
        });
    }

    private async Task<YamuxHeader> ReadHeaderAsync(IReader reader)
    {
        byte[] headerData = (await reader.ReadAsync(12)).ToArray();
        YamuxHeader header = YamuxHeader.FromBytes(headerData);
        _logger?.LogDebug("Read header, stream-{0} type={1} flags={2}", header.StreamID, header.Type, header.Flags);
        return header;
    }

    private async Task WriteHeaderAsync(IWriter writer, YamuxHeader header, ReadOnlySequence<byte> data = default)
    {
        byte[] headerBuffer = new byte[12];
        header.Length = (int)data.Length;
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogDebug("Write header, stream-{0} type={1} flags={2}{3}", header.StreamID, header.Type, header.Flags,
            header.Type != YamuxHeaderType.Data ? "" : " data: " + Encoding.ASCII.GetString(data.ToArray()));
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
