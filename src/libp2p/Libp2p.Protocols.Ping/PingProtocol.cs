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
        byte[] byteArray = new byte[PayloadLength];
        _random.NextBytes(byteArray.AsSpan(0, PayloadLength));
        ReadOnlySequence<byte> bytes = new(byteArray);

        _logger?.LogPing(context.RemotePeer.Address);
        await channel.WriteAsync(bytes);

        _logger?.ReadingPong(context.RemotePeer.Address);
        ReadOnlySequence<byte> response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll);

        _logger?.VerifyingPong(context.RemotePeer.Address);
        if (!byteArray[0..PayloadLength].SequenceEqual(response.ToArray()))
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

        while (await channel.CanReadAsync())
        {
            _logger?.ReadingPing(context.RemotePeer.Address);
            ReadOnlySequence<byte> request = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll);
            byte[] byteArray = request.ToArray();
            ReadOnlySequence<byte> bytes = new(byteArray);

            _logger?.ReturningPong(context.RemotePeer.Address);
            await channel.WriteAsync(bytes);
        }

        _logger?.PingFinished(context.RemotePeer.Address);
    }
}
