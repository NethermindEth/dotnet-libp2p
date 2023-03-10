// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text;
using Nethermind.Libp2p.Core;
using Microsoft.Extensions.Logging;

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol : SymmetricProtocol, IProtocol
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
        Dictionary<int, ChannelState?> channels = new();
        channels[0] = new ChannelState { State = 1 };
        await WriteHeader(channel.Writer,
            new YamuxHeader { Flags = YamuxHeaderFlags.Syn, Type = YamuxHeaderType.Ping, StreamID = 0 });

        Task.Run(async () =>
        {
            while (true)
            {
                YamuxHeader header = await ReadHeader(channel.Reader);
                byte[] data = null;
                if (header.Type == YamuxHeaderType.Data && header.Flags == 0)
                {
                    data = new byte[header.Length];
                    await channel.Reader.ReadAsync(data);
                }

                if (header.StreamID == 0)
                {
                    if (header.Flags == YamuxHeaderFlags.Syn)
                    {
                        await WriteHeader(channel.Writer,
                            new YamuxHeader
                                { Flags = YamuxHeaderFlags.Ack, Type = YamuxHeaderType.Ping, StreamID = 0 });
                    }

                    if (header.Flags == YamuxHeaderFlags.Ack)
                    {
                        channels[header.StreamID].State = 2;

                        Task.Run(async () =>
                        {
                            channelFactory.Connected(context.RemotePeer as IRemotePeer);
                            foreach (IChannelRequest request in channelFactory.SubDialRequests
                                         .GetConsumingEnumerable())
                            {
                                streamIdCounter += 2;
                                channels[streamIdCounter] = new ChannelState { State = 1, Request = request };
                                _ = WriteHeader(channel.Writer,
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
                    await WriteHeader(channel.Writer,
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
                                await WriteHeader(channel.Writer,
                                    new YamuxHeader
                                    {
                                        Flags = YamuxHeaderFlags.Fin, Type = YamuxHeaderType.WindowUpdate,
                                        StreamID = streamId
                                    });
                            }

                            _logger?.LogDebug("Close, stream-{0}", streamId);
                        });
                        channels[header.StreamID].Channel = chan;
                        Task.Run(async () =>
                        {
                            while (true)
                            {
                                byte[] buf = new byte[65526];
                                int size = await channels[streamId].Channel.Reader.ReadAsync(buf, false);
                                await WriteHeader(channel.Writer,
                                    new YamuxHeader
                                    {
                                        Type = YamuxHeaderType.Data, Length = size, StreamID = streamId
                                    }, buf[..size]);
                            }
                        });
                    }

                    if (header.Type == YamuxHeaderType.Data && header.Flags == 0)
                    {
                        await channels[header.StreamID].Channel.Writer.WriteAsync(data);
                        _logger?.LogDebug("Data, stream-{0}: {1}", header.StreamID,
                            Encoding.ASCII.GetString(data));
                    }

                    if (header.Flags == YamuxHeaderFlags.Fin)
                    {
                    }
                }
            }
        });
    }

    private async Task<YamuxHeader> ReadHeader(IReader reader)
    {
        byte[] sizeBuf = new byte[12];
        int bytesRead = await reader.ReadAsync(sizeBuf);
        YamuxHeader header = MarshalYamuxHeader(sizeBuf);
        _logger?.LogDebug("Read a header, stream-{0} type={1} flags={2}", header.StreamID, header.Type, header.Flags);
        return header;
    }

    private async Task WriteHeader(IWriter writer, YamuxHeader header, byte[]? data = null)
    {
        byte[] sizeBuf = new byte[12 + (data?.Length ?? 0)];
        header.Length = data?.Length ?? 0;
        UnmarshalYamuxHeader(sizeBuf, ref header);
        data?.CopyTo(sizeBuf, 12);
        _logger?.LogInformation(
            $"Write header, stream-{header.StreamID} type={header.Type} flags={header.Flags} {(header.Type != YamuxHeaderType.Data
                ? ""
                : "\ndata " + Encoding.ASCII.GetString(data))}");
        await writer.WriteAsync(sizeBuf);
    }

    private static YamuxHeader MarshalYamuxHeader(Span<byte> data)
    {
        short flags = BinaryPrimitives.ReadInt16BigEndian(data[2..]);
        int streamId = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        int length = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        return new YamuxHeader
        {
            Version = data[0], Type = (YamuxHeaderType)data[1], Flags = (YamuxHeaderFlags)flags, StreamID = streamId,
            Length = length
        };
    }

    private static void UnmarshalYamuxHeader(Span<byte> data, ref YamuxHeader header)
    {
        data[0] = header.Version;
        data[1] = (byte)header.Type;
        BinaryPrimitives.WriteInt16BigEndian(data[2..], (short)header.Flags);
        BinaryPrimitives.WriteInt32BigEndian(data[4..], header.StreamID);
        BinaryPrimitives.WriteInt32BigEndian(data[8..], header.Length);
    }


    private class ChannelState
    {
        public int State { get; set; }
        public IChannel? Channel { get; set; }
        public IChannelRequest? Request { get; set; }
    }
}
