// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using System.Buffers;
using System.Text;

namespace Nethermind.Libp2p.Protocols;

public class YamuxProtocol : SymmetricProtocol, IDuplexProtocol
{
    private const int HeaderLength = 12;
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

        _logger?.LogInformation("Yamux as {role}", isListener ? "listener" : "dialer");
        int streamIdCounter = isListener ? 2 : 1;

        Dictionary<int, ChannelState> channels = new();

        if (!isListener)
        {
            await WriteHeaderAsync(channel,
                new YamuxHeader { Flags = YamuxHeaderFlags.Syn, Type = YamuxHeaderType.Ping, StreamID = 0 });
        }

        context.Connected(context.RemotePeer);

        _ = Task.Run(async () =>
        {
            foreach (IChannelRequest request in context.GetBlockingSubDialRequestsEnumerable())
            {
                int streamId = streamIdCounter;
                streamIdCounter += 2;
                _logger?.LogDebug("Trying to dial with protocol {proto} via stream-{streamId}", request.SubProtocol?.Id, streamId);
                channels[streamId] = new ChannelState { Request = request };
                await WriteHeaderAsync(channel,
                    new YamuxHeader
                    {
                        Flags = YamuxHeaderFlags.Syn,
                        Type = YamuxHeaderType.Data,
                        StreamID = streamId,
                    });
                ActivateUpchannel(streamId, request);
            }
        });

        while (!channel.IsClosed)
        {
            YamuxHeader header = await ReadHeaderAsync(channel, token: channel.Token);
            ReadOnlySequence<byte> data = default;

            if (header.StreamID is 0)
            {
                if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                {
                    _logger?.LogDebug("Confirming session stream");
                    _ = WriteHeaderAsync(channel,
                        new YamuxHeader
                        {
                            Flags = YamuxHeaderFlags.Ack,
                            Type = YamuxHeaderType.Data,
                            StreamID = header.StreamID
                        });
                }

                if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst || (header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
                {
                    _logger?.LogDebug("Closing all streams");

                    foreach (ChannelState channelState in channels.Values)
                    {
                        await channelState.Channel?.CloseAsync();
                    }

                    await channel.CloseAsync();
                    return;
                }

                continue;
            }

            if (header is { Type: YamuxHeaderType.Data, Length: not 0 })
            {
                data = new ReadOnlySequence<byte>((await channel.ReadAsync(header.Length)).ToArray());
                _logger?.LogDebug("Recv data, stream-{0}, len={1}, data: {data}",
                    header.StreamID, data.Length,
                    Encoding.ASCII.GetString(data.ToArray().Select(c => c == 0x1b || c == 0x07 ? (byte)0x2e : c).ToArray()));
            }

            if (channels.TryAdd(header.StreamID, new()) || (header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
            {
                _logger?.LogDebug("Request for a stream");
                _ = WriteHeaderAsync(channel,
                    new YamuxHeader
                    {
                        Flags = YamuxHeaderFlags.Ack,
                        Type = YamuxHeaderType.Data,
                        StreamID = header.StreamID
                    });
            }

            if (channels[header.StreamID].Channel is null)
            {
                ActivateUpchannel(header.StreamID, null);
                _logger?.LogDebug("Channel activated for stream-{streamId}", header.StreamID);
            }

            if (header.Type == YamuxHeaderType.Data)
            {
                _logger?.LogDebug("Write data to upchannel, stream-{0}, len={1}", header.StreamID, data.Length);
                await channels[header.StreamID].Channel!.WriteAsync(data);
            }

            if ((header.Flags & YamuxHeaderFlags.Fin) == YamuxHeaderFlags.Fin)
            {
                _ = channels[header.StreamID].Channel?.CloseAsync();
                _logger?.LogDebug("Fin, stream-{0}", header.StreamID);
            }

            if ((header.Flags & YamuxHeaderFlags.Rst) == YamuxHeaderFlags.Rst)
            {
                _ = channels[header.StreamID].Channel?.CloseAsync();
                _logger?.LogDebug("Rst, stream-{0}", header.StreamID);
            }
        }

        void ActivateUpchannel(int streamId, IChannelRequest? channelRequest)
        {
            if (channels[streamId].Channel is not null)
            {
                return;
            }

            bool isListenerChannel = isListener ^ (streamId % 2 == 0);

            _logger?.LogDebug("Create chan for stream-{0} isListener = {1}", streamId, isListenerChannel);
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

            upChannel.OnClose(async () =>
            {
                await WriteHeaderAsync(channel,
                    new YamuxHeader
                    {
                        Flags = YamuxHeaderFlags.Fin,
                        Type = YamuxHeaderType.Data,
                        StreamID = streamId
                    });
                _logger?.LogDebug("Close, stream-{0}", streamId);
            });

            _ = Task.Run(async () =>
            {
                while (!channel.IsClosed)
                {
                    ReadOnlySequence<byte> upData =
                        await channels[streamId].Channel!.ReadAsync(0, ReadBlockingMode.WaitAny,
                            channel.Token);
                    _logger?.LogDebug("Read data from upchannel, stream-{0}, len={1}", streamId, upData.Length);
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
    }

    private async Task<YamuxHeader> ReadHeaderAsync(IReader reader, CancellationToken token = default)
    {
        byte[] headerData = (await reader.ReadAsync(HeaderLength, token: token)).ToArray();
        YamuxHeader header = YamuxHeader.FromBytes(headerData);
        _logger?.LogDebug("Read, stream-{streamId} type={type} flags={flags}{dataLength}", header.StreamID, header.Type, header.Flags,
            header.Type == YamuxHeaderType.Data ? $", {header.Length}B content" : "");
        return header;
    }

    private async Task WriteHeaderAsync(IWriter writer, YamuxHeader header, ReadOnlySequence<byte> data = default)
    {
        byte[] headerBuffer = new byte[HeaderLength];
        header.Length = (int)data.Length;
        YamuxHeader.ToBytes(headerBuffer, ref header);

        _logger?.LogDebug("Write, stream-{streamId} type={type} flags={flags}{dataLength}", header.StreamID, header.Type, header.Flags,
             header.Type == YamuxHeaderType.Data ? $", {header.Length}B content" : "");
        await writer.WriteAsync(
            data.Length == 0 ? new ReadOnlySequence<byte>(headerBuffer) : data.Prepend(headerBuffer));
    }

    private struct ChannelState
    {
        public ChannelState(IChannel? channel, IChannelRequest? request = default)
        {
            Channel = channel;
            Request = request;
        }

        public IChannel? Channel { get; set; }
        public IChannelRequest? Request { get; set; }
    }
}
