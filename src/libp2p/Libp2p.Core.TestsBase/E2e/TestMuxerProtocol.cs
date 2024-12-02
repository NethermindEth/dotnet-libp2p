
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase.Dto;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Org.BouncyCastle.Utilities.Encoders;
using System.Buffers;

class TestMuxerProtocol(ChannelBus bus, ILoggerFactory? loggerFactory = null) : IProtocol
{
    private const string id = "test-muxer";

    private readonly ILogger? logger = loggerFactory?.CreateLogger(id);

    public string Id => id;

    public async Task DialAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}: Dial async");
        context.Connected(context.RemotePeer);
        await Task.Run(() => HandleRemote(bus.Dial(context.LocalPeer.Identity.PeerId, context.RemotePeer.Address.GetPeerId()!), upChannelFactory!, context));
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory? upChannelFactory, IPeerContext context)
    {
        context.ListenerReady();
        logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}: Listen async");
        await foreach (var item in bus.GetIncomingRequests(context.LocalPeer.Identity.PeerId))
        {
            logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}: Listener handles new con");
            _ = HandleRemote(item, upChannelFactory!, context, true);
        }
    }

    private async Task HandleRemote(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context, bool isListen = false)
    {
        uint counter = isListen ? 1u : 0u;
        Dictionary<uint, MuxerChannel> chans = [];

        string peer = "";
        context = context.Fork();

        if (isListen)
        {
            peer = await downChannel.ReadLineAsync();
            await downChannel.WriteLineAsync(context.LocalPeer.Identity.PeerId!.ToString());
            logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}: Listener handles remote {peer}");
        }
        else
        {
            await downChannel.WriteLineAsync(context.LocalPeer.Identity.PeerId!.ToString());
            peer = await downChannel.ReadLineAsync();
            logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}: Dialer handles remote {peer}");
        }

        context.RemotePeer.Address = $"/p2p/{peer}";

        string logPrefix = $"{context.LocalPeer.Identity.PeerId}<>{peer}";

        _ = Task.Run(async () =>
        {
            foreach (var item in context.SubDialRequests.GetConsumingEnumerable())
            {
                uint chanId = Interlocked.Add(ref counter, 2);
                logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}({chanId}): Sub-request {item.SubProtocol} {item.CompletionSource is not null} from {context.RemotePeer.Address.GetPeerId()}");

                chans[chanId] = new MuxerChannel { Tcs = item.CompletionSource };
                var response = new MuxerPacket()
                {
                    ChannelId = chanId,
                    Type = MuxerPacketType.NewStreamRequest,
                    Protocols = { item.SubProtocol!.Id }
                };

                logger?.LogDebug($"{logPrefix}({response.ChannelId}): > Packet {response.Type} {string.Join(",", response.Protocols)} {response.Data?.Length ?? 0}");

                _ = downChannel.WriteSizeAndProtobufAsync(response);
            }
            logger?.LogDebug($"{context.LocalPeer.Identity.PeerId}: SubDialRequests End");

        });

        while (true)
        {
            try
            {
                logger?.LogDebug($"{logPrefix}: < READY({(isListen ? "list" : "dial")})");

                var packet = await downChannel.ReadPrefixedProtobufAsync(MuxerPacket.Parser);

                logger?.LogDebug($"{logPrefix}({packet.ChannelId}): < Packet {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                switch (packet.Type)
                {
                    case MuxerPacketType.NewStreamRequest:
                        IProtocol? selected = null;
                        foreach (var proto in packet.Protocols)
                        {
                            selected = upChannelFactory.SubProtocols.FirstOrDefault(x => x.Id == proto);
                            if (selected is not null) break;
                        }
                        if (selected is not null)
                        {
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Matched {selected}");
                            var response = new MuxerPacket()
                            {
                                ChannelId = packet.ChannelId,
                                Type = MuxerPacketType.NewStreamResponse,
                                Protocols =
                            {
                                selected.Id
                            }
                            };

                            var req = new ChannelRequest { SubProtocol = selected };

                            IChannel upChannel = upChannelFactory.SubListen(context, req);
                            chans[packet.ChannelId] = new MuxerChannel { UpChannel = upChannel };
                            _ = HandleUpchannelData(downChannel, chans, packet.ChannelId, upChannel, logPrefix);

                            logger?.LogDebug($"{logPrefix}({response.ChannelId}): > Packet {response.Type} {string.Join(",", response.Protocols)} {response.Data?.Length ?? 0}");

                            _ = downChannel.WriteSizeAndProtobufAsync(response);
                        }
                        else
                        {
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): No match {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                            var response = new MuxerPacket()
                            {
                                ChannelId = packet.ChannelId,
                                Type = MuxerPacketType.NewStreamResponse,
                            };

                            logger?.LogDebug($"{logPrefix}({response.ChannelId}): > Packet {response.Type} {string.Join(",", response.Protocols)} {response.Data?.Length ?? 0}");

                            _ = downChannel.WriteSizeAndProtobufAsync(response);
                        }
                        break;
                    case MuxerPacketType.NewStreamResponse:
                        if (packet.Protocols.Any())
                        {
                            var req = new ChannelRequest { SubProtocol = upChannelFactory.SubProtocols.FirstOrDefault(x => x.Id == packet.Protocols.First()) };
                            IChannel upChannel = upChannelFactory.SubDial(context, req);
                            chans[packet.ChannelId].UpChannel = upChannel;
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Start upchanel with {req.SubProtocol}");
                            _ = HandleUpchannelData(downChannel, chans, packet.ChannelId, upChannel, logPrefix);
                        }
                        else
                        {
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): No protocols = no upchanel");
                        }
                        break;
                    case MuxerPacketType.Data:
                        logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Data to upchanel {packet.Data?.Length ?? 0} {Hex.ToHexString(packet.Data?.ToByteArray() ?? [])}");
                        _ = chans[packet.ChannelId].UpChannel!.WriteAsync(new ReadOnlySequence<byte>(packet.Data.ToByteArray()));
                        break;
                    case MuxerPacketType.CloseWrite:
                        logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Remote EOF");

                        chans[packet.ChannelId].RemoteClosedWrites = true;
                        _ = chans[packet.ChannelId].UpChannel!.WriteEofAsync();
                        break;
                    default:
                        break;
                }
            }
            catch
            {


            }

        }
    }

    private Task HandleUpchannelData(IChannel downChannel, Dictionary<uint, MuxerChannel> chans, uint channelId, IChannel upChannel, string logPrefix)
    {
        return Task.Run(async () =>
        {
            try
            {
                await foreach (var item in upChannel.ReadAllAsync())
                {
                    var data = item.ToArray();
                    logger?.LogDebug($"{logPrefix}({channelId}): Upchannel data {data.Length} {Hex.ToHexString(data, false)}");

                    var packet = new MuxerPacket()
                    {
                        ChannelId = channelId,
                        Type = MuxerPacketType.Data,
                        Data = ByteString.CopyFrom(item.ToArray())
                    };

                    logger?.LogDebug($"{logPrefix}({packet.ChannelId}): > Packet {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                    _ = downChannel.WriteSizeAndProtobufAsync(packet);
                }
                if (chans[channelId].RemoteClosedWrites)
                {
                    logger?.LogDebug($"{logPrefix}({channelId}): Upchannel dial/listen complete");
                    chans[channelId].Tcs?.SetResult();
                }
                logger?.LogDebug($"{logPrefix}({channelId}): Upchannel write close");

                {
                    var packet = new MuxerPacket()
                    {
                        ChannelId = channelId,
                        Type = MuxerPacketType.CloseWrite,
                    };

                    logger?.LogDebug($"{logPrefix}({packet.ChannelId}): > Packet {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                    _ = downChannel.WriteSizeAndProtobufAsync(packet);
                }
            }
            catch
            {

            }
        });
    }

    class MuxerChannel
    {
        public IChannel? UpChannel { get; set; }
        public TaskCompletionSource? Tcs { get; set; }
        public bool RemoteClosedWrites { get; set; }
    }
}
