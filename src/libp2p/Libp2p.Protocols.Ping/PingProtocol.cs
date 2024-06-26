// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Ping;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/ping/ping.md
/// </summary>
public class PingProtocol : IProtocol
{
    private const int PayloadLength = 32;

    public string Id => "/ipfs/ping/1.0.0";
    private readonly Random _random = new();
    private readonly ILogger<PingProtocol>? _logger;

    public PingProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PingProtocol>();
    }

    public async Task DialAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        byte[] ping = new byte[PayloadLength];
        _random.NextBytes(ping.AsSpan(0, PayloadLength));
        ReadOnlySequence<byte> bytes = new(ping);

        _logger?.LogPing(context.RemotePeer.Address);
        await channel.WriteAsync(bytes);
        _logger?.LogTrace("Sent ping: {ping}", Convert.ToHexString(ping));

        _logger?.ReadingPong(context.RemotePeer.Address);
        ReadOnlySequence<byte> response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll).OrThrow();
        _logger?.LogTrace("Received pong: {ping}", Convert.ToHexString(ping));

        _logger?.VerifyingPong(context.RemotePeer.Address);
        if (!ping[0..PayloadLength].SequenceEqual(response.ToArray()))
        {
            _logger?.PingFailed(context.RemotePeer.Address);
            throw new ApplicationException();
        }

        _logger?.LogPinged(context.RemotePeer.Address);
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        _logger?.PingListenStarted(context.RemotePeer.Address);

        while (true)
        {
            _logger?.ReadingPing(context.RemotePeer.Address);
            ReadResult read = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAny);
            if (read.Result != IOResult.Ok)
            {
                break;
            }

            byte[] ping = read.Data.ToArray();
            _logger?.LogTrace("Received ping: {ping}", Convert.ToHexString(ping));

            _logger?.ReturningPong(context.RemotePeer.Address);
            await channel.WriteAsync(new ReadOnlySequence<byte>(ping));
            _logger?.LogTrace("Sent pong: {ping}", Convert.ToHexString(ping));
        }

        _logger?.PingFinished(context.RemotePeer.Address);
    }
}
