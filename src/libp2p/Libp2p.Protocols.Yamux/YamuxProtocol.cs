// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols.Yamux;
using System.Buffers;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Libp2p.Protocols.Yamux.Tests")]

namespace Nethermind.Libp2p.Protocols;

public partial class YamuxProtocol : SymmetricProtocol, IConnectionProtocol
{
    public const int ProtocolInitialWindowSize = 256 * 1024;

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
        using var scope = _logger?.BeginScope("Context id {ctx}", context.Id);
        _logger?.LogInformation("Ctx({ctx}): {mode} {peer}", context.Id, isListener ? "Listen" : "Dial", context.State.RemoteAddress);

        TaskAwaiter downChannelAwaiter = channel.GetAwaiter();

        Dictionary<int, ChannelState> channels = [];
        INewSessionContext? session = null;

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

            uint pingCounter = 0;

            using Timer timer = new((s) =>
            {
                _ = WriteHeaderAsync(session.Id, channel, new YamuxHeader { Type = YamuxHeaderType.Ping, Flags = YamuxHeaderFlags.Syn, Length = (int)(++pingCounter % int.MaxValue) });
            }, null, PingDelay, PingDelay);

            _ = Task.Run(async () =>
            {
                await waitForSession.WaitAsync();
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
                YamuxHeader header = await ReadHeaderAsync(session?.Id ?? "nil", channel);
                ReadOnlySequence<byte> data = default;

                if (header.Type > YamuxHeaderType.GoAway)
                {
                    _logger?.LogWarning("Ctx({ctx}): Bad packet received, type: {}", session?.Id ?? "nil", header.Type);
                    await WriteGoAwayAsync(session?.Id ?? "nil", channel, SessionTerminationCode.ProtocolError);
                    return;
                }

                if (header.StreamID is 0)
                {
                    if (header.Type == YamuxHeaderType.Ping)
                    {
                        if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn)
                        {
                            _ = WriteHeaderAsync(session?.Id ?? "nil", channel,
                                new YamuxHeader
                                {
                                    Flags = YamuxHeaderFlags.Ack,
                                    Type = YamuxHeaderType.Ping,
                                    Length = header.Length,
                                });

                            _logger?.LogDebug("Ctx({ctx}): Ping received and acknowledged", session.Id);
                        }
                        continue;
                    }

                    if (header.Type == YamuxHeaderType.GoAway)
                    {
                        _logger?.LogDebug("Ctx({ctx}): Closing all streams", session?.Id ?? "nil");

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
                else if (isListener && session is null)
                {
                    session = context.UpgradeToSession();
                    _logger?.LogInformation("Ctx({ctx}): Session created by listener for {peer}", session.Id, session.State.RemoteAddress);
                    waitForSession.Release();
                }

                if ((header.Flags & YamuxHeaderFlags.Syn) == YamuxHeaderFlags.Syn && !channels.ContainsKey(header.StreamID))
                {
                    channels[header.StreamID] = CreateUpchannel(session.Id, header.StreamID, YamuxHeaderFlags.Ack, new UpgradeOptions());
                }

                if (!channels.ContainsKey(header.StreamID))
                {
                    if (header.Type == YamuxHeaderType.Data && header.Length > 0)
                    {
                        await channel.ReadAsync(header.Length);
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

                    data = await channel.ReadAsync(header.Length).OrThrow();

                    bool spent = channels[header.StreamID].LocalWindow.TrySpend((int)data.Length);
                    if (!spent)
                    {
                        _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Window spent out of budget", session.Id, header.StreamID);
                        await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.InternalError);
                        return;
                    }

                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Spent window, was {available}, became {new}", session.Id,
                           header.StreamID, available, channels[header.StreamID].LocalWindow.Available);

                    _ = channels[header.StreamID].Channel!.WriteAsync(data).AsTask().ContinueWith((t) =>
                    {
                        if (!t.IsCompletedSuccessfully)
                        {
                            _logger?.LogWarning("Ctx({ctx}), stream {stream id}: Failed to send upstream", session.Id, header.StreamID);
                        }

                        ExtendWindow(channel, session.Id, header.StreamID, t.Result);
                    });
                }

                if (header.Type == YamuxHeaderType.WindowUpdate && header.Length != 0)
                {
                    int oldSize = channels[header.StreamID].RemoteWindow.Available;
                    int newSize = channels[header.StreamID].RemoteWindow.Extend(header.Length);
                    _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Window update requested: {old} => {new}", session.Id, header.StreamID, oldSize, newSize);
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

            await WriteGoAwayAsync(session.Id, channel, SessionTerminationCode.Ok);

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

                        await foreach (ReadOnlySequence<byte> upData in upChannel.ReadAllAsync())
                        {
                            _logger?.LogDebug("Ctx({ctx}), stream {stream id}: Receive from upchannel, length={length}", contextId, streamId, upData.Length);

                            for (int i = 0; i < upData.Length;)
                            {
                                int sendingSize = await state.RemoteWindow.SpendOrWait((int)upData.Length - i, state.Channel!.CancellationToken);

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
        finally
        {
            session?.Dispose();
        }

        foreach (ChannelState upChannel in channels.Values)
        {
            _ = upChannel.Channel?.CloseAsync();
        }

        void ExtendWindow(IChannel channel, string sessionId, int streamId, IOResult result)
        {
            if (result == IOResult.Ok)
            {
                int extendedBy = channels[streamId].LocalWindow.ExtendIfNeeded();
                if (extendedBy is not 0)
                {
                    _ = WriteHeaderAsync(sessionId, channel,
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
}
