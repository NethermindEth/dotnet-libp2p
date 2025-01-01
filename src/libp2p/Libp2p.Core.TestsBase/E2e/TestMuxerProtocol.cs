
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.TestsBase.Dto;
using Nethermind.Libp2p.Core.TestsBase.E2e;
using Org.BouncyCastle.Utilities.Encoders;
using System.Buffers;

class TestMuxerProtocol(ChannelBus bus, ILoggerFactory? loggerFactory = null) : ITransportProtocol
{
    private const string id = "test-muxer";

    private readonly ILogger? logger = loggerFactory?.CreateLogger(id);

    public string Id => id;
    public static Multiaddress[] GetDefaultAddresses(PeerId peerId) => [$"/p2p/{peerId}"];
    public static bool IsAddressMatch(Multiaddress addr) => true;

    public async Task DialAsync(ITransportContext context, Multiaddress remoteAddr, CancellationToken token)
    {
        logger?.LogDebug($"{context.Peer.Identity.PeerId}: Dial async");

        //await Task.Run(async () =>
        //{
        IChannel chan = bus.Dial(context.Peer.Identity.PeerId, remoteAddr.GetPeerId()!);
        using INewConnectionContext connection = context.CreateConnection();
        connection.State.RemoteAddress = remoteAddr;

        await HandleRemote(chan, connection, context);
        //});
    }

    public async Task ListenAsync(ITransportContext context, Multiaddress listenAddr, CancellationToken token)
    {
        context.ListenerReady(listenAddr);
        logger?.LogDebug($"{context.Peer.Identity.PeerId}: Listen async");
        await foreach (IChannel item in bus.GetIncomingRequests(context.Peer.Identity.PeerId))
        {
            _ = Task.Run(async () =>
            {
                INewConnectionContext connection = context.CreateConnection();
                logger?.LogDebug($"{context.Peer.Identity.PeerId}: Listener handles new con");
                try
                {
                    await HandleRemote(item, connection, context, true);
                }
                catch (SessionExistsException)
                {
                    logger?.LogDebug($"{context.Peer.Identity.PeerId}: Listener rejected initiation of a redundant session");
                }
                catch (Exception e)
                {
                    logger?.LogError(e, $"{context.Peer.Identity.PeerId}: Listener exception");
                }
            }, token);
        }
    }

    private async Task HandleRemote(IChannel downChannel, INewConnectionContext connection, ITransportContext context, bool isListen = false)
    {
        uint counter = isListen ? 1u : 0u;
        Dictionary<uint, MuxerChannel> chans = [];

        PublicKey? remotePublicKey;
        PeerId? remotePeerId;

        if (isListen)
        {
            remotePublicKey = await downChannel.ReadPrefixedProtobufAsync(PublicKey.Parser);
            remotePeerId = new PeerId(remotePublicKey);
            await downChannel.WriteSizeAndProtobufAsync(context.Peer.Identity.PublicKey);
            logger?.LogDebug($"{context.Peer.Identity.PeerId}: Listener handles remote {remotePeerId}");
        }
        else
        {
            await downChannel.WriteSizeAndProtobufAsync(context.Peer.Identity.PublicKey);
            remotePublicKey = await downChannel.ReadPrefixedProtobufAsync(PublicKey.Parser);
            remotePeerId = new PeerId(remotePublicKey);
            logger?.LogDebug($"{context.Peer.Identity.PeerId}: Dialer handles remote {remotePeerId}");
        }

        connection.State.RemotePublicKey = remotePublicKey;
        connection.State.RemoteAddress = $"/p2p/{remotePeerId}";
        using INewSessionContext? session = connection.UpgradeToSession();

        string logPrefix = $"{context.Peer.Identity.PeerId}<>{remotePeerId}";

        _ = Task.Run(() =>
        {
            foreach (UpgradeOptions item in session.DialRequests)
            {
                uint chanId = Interlocked.Add(ref counter, 2);
                logger?.LogDebug($"{context.Peer.Identity.PeerId}({chanId}): Sub-request {item.SelectedProtocol} {item.CompletionSource is not null} to call {connection.State.RemoteAddress.GetPeerId()}");

                chans[chanId] = new MuxerChannel { Tcs = item.CompletionSource, Argument = item.Argument };
                MuxerPacket response = new()
                {
                    ChannelId = chanId,
                    Type = MuxerPacketType.NewStreamRequest,
                    Protocols = { item.SelectedProtocol!.Id }
                };

                logger?.LogDebug($"{logPrefix}({response.ChannelId}): > Packet {response.Type} {string.Join(",", response.Protocols)} {response.Data?.Length ?? 0}");

                _ = downChannel.WriteSizeAndProtobufAsync(response);
            }
            logger?.LogDebug($"{context.Peer.Identity.PeerId}: SubDialRequests End");
            return Task.CompletedTask;
        });

        while (true)
        {
            try
            {
                logger?.LogDebug($"{logPrefix}: < READY({(isListen ? "list" : "dial")})");

                MuxerPacket packet = await downChannel.ReadPrefixedProtobufAsync(MuxerPacket.Parser);

                logger?.LogDebug($"{logPrefix}({packet.ChannelId}): < Packet {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                switch (packet.Type)
                {
                    case MuxerPacketType.NewStreamRequest:
                        IProtocol? selected = null;
                        foreach (string? proto in packet.Protocols)
                        {
                            selected = session.SubProtocols.FirstOrDefault(x => x.Id == proto);
                            if (selected is not null) break;
                        }
                        if (selected is not null)
                        {
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Matched {selected}");
                            MuxerPacket response = new()
                            {
                                ChannelId = packet.ChannelId,
                                Type = MuxerPacketType.NewStreamResponse,
                                Protocols =
                            {
                                selected.Id
                            }
                            };

                            UpgradeOptions req = new() { SelectedProtocol = selected, ModeOverride = UpgradeModeOverride.Listen };

                            IChannel upChannel = session.Upgrade(selected, req);
                            chans[packet.ChannelId] = new MuxerChannel { UpChannel = upChannel };
                            _ = HandleUpchannelData(downChannel, chans, packet.ChannelId, upChannel, logPrefix);

                            logger?.LogDebug($"{logPrefix}({response.ChannelId}): > Packet {response.Type} {string.Join(",", response.Protocols)} {response.Data?.Length ?? 0}");

                            _ = downChannel.WriteSizeAndProtobufAsync(response);
                        }
                        else
                        {
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): No match {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                            MuxerPacket response = new()
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
                            UpgradeOptions req = new() { SelectedProtocol = session.SubProtocols.FirstOrDefault(x => x.Id == packet.Protocols.First()), CompletionSource = chans[packet.ChannelId].Tcs, Argument = chans[packet.ChannelId].Argument, ModeOverride = UpgradeModeOverride.Dial };
                            IChannel upChannel = session.Upgrade(session.SubProtocols.FirstOrDefault(x => x.Id == packet.Protocols.First())!, req);
                            chans[packet.ChannelId].UpChannel = upChannel;
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Start upchanel with {req.SelectedProtocol}");
                            _ = HandleUpchannelData(downChannel, chans, packet.ChannelId, upChannel, logPrefix);
                        }
                        else
                        {
                            logger?.LogDebug($"{logPrefix}({packet.ChannelId}): No protocols = no upchanel");
                        }
                        break;
                    case MuxerPacketType.Data:
                        if (packet.Data is null or [])
                        {
                            logger?.LogWarning($"{logPrefix}({packet.ChannelId}): Empty data received");
                            break;
                        }
                        logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Data to upchanel {packet.Data.Length} {Hex.ToHexString(packet.Data.ToByteArray())}");
                        _ = chans.GetValueOrDefault(packet.ChannelId)?.UpChannel?.WriteAsync(new ReadOnlySequence<byte>(packet.Data.ToByteArray()));
                        break;
                    case MuxerPacketType.CloseWrite:
                        logger?.LogDebug($"{logPrefix}({packet.ChannelId}): Remote EOF");

                        lock (chans[packet.ChannelId])
                        {
                            chans[packet.ChannelId].RemoteClosedWrites = true;

                            _ = chans[packet.ChannelId].UpChannel?.WriteEofAsync();

                            if (chans[packet.ChannelId].LocalClosedWrites)
                            {
                                //chans[packet.ChannelId].Tcs?.SetResult(null);
                                _ = chans[packet.ChannelId].UpChannel?.CloseAsync();
                                chans.Remove(packet.ChannelId);
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
            catch (Exception e)
            {
                logger?.LogError(e, $"{logPrefix}: Muxer listener exception");
            }

        }
    }

    private Task HandleUpchannelData(IChannel downChannel, Dictionary<uint, MuxerChannel> chans, uint channelId, IChannel upChannel, string logPrefix)
    {
        return Task.Run(async () =>
        {
            try
            {
                await foreach (ReadOnlySequence<byte> item in upChannel.ReadAllAsync())
                {
                    byte[] data = item.ToArray();
                    logger?.LogDebug($"{logPrefix}({channelId}): Upchannel data {data.Length} {Hex.ToHexString(data, false)}");

                    MuxerPacket packet = new()
                    {
                        ChannelId = channelId,
                        Type = MuxerPacketType.Data,
                        Data = ByteString.CopyFrom(item.ToArray())
                    };

                    logger?.LogDebug($"{logPrefix}({packet.ChannelId}): > Packet {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                    _ = downChannel.WriteSizeAndProtobufAsync(packet);
                }
                lock (chans[channelId])
                {
                    chans[channelId].LocalClosedWrites = true;

                    if (chans[channelId].RemoteClosedWrites)
                    {
                        logger?.LogDebug($"{logPrefix}({channelId}): Upchannel dial/listen complete");
                        //chans[channelId].Tcs?.SetResult(null);
                        _ = upChannel.CloseAsync();
                        chans.Remove(channelId);
                    }

                    logger?.LogDebug($"{logPrefix}({channelId}): Upchannel write close");

                    {
                        MuxerPacket packet = new()
                        {
                            ChannelId = channelId,
                            Type = MuxerPacketType.CloseWrite,
                        };

                        logger?.LogDebug($"{logPrefix}({packet.ChannelId}): > Packet {packet.Type} {string.Join(",", packet.Protocols)} {packet.Data?.Length ?? 0}");

                        _ = downChannel.WriteSizeAndProtobufAsync(packet);
                    }
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
        public TaskCompletionSource<object?>? Tcs { get; set; }
        public bool RemoteClosedWrites { get; set; }
        public bool LocalClosedWrites { get; set; }
        public object? Argument { get; internal set; }
    }
}
