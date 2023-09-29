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

    public string Id => "/ipfs/ping/1.0.0"; // TODO: order in class: fields, constructors, properties, methods?
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

        _logger?.ReadingPong();
        ReadOnlySequence<byte> response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll);

        _logger?.VerifyingPong();
        if (!byteArray[0..PayloadLength].SequenceEqual(response.ToArray()))
        {
            // TODO: do we need log context.RemotePeer or context.RemotePeer.Address?
            // _logger?.LogWarning("Wrong response to ping from {from}", context.RemotePeer);

            // TODO: if we throw exception, probably LogLevel bigger than warning?
            _logger?.PingFailed(context.RemotePeer.Address);
            throw new ApplicationException(); // TODO: add specific exception?
        }

        _logger?.LogPinged(); // TODO: add context.RemotePeer.Address?
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        // TODO: do we need log context.RemotePeer or context.RemotePeer.Address?
        // _logger?.LogDebug("Ping listen started from {remotePeer}", context.RemotePeer);
        _logger?.PingListenStarted(context.RemotePeer.Address);

        while (!channel.IsClosed)
        {
            _logger?.ReadingPing();
            ReadOnlySequence<byte> request = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll);
            byte[] byteArray = request.ToArray(); // TODO: do we need convert to array and then again to ReadOnlySequence?
            ReadOnlySequence<byte> bytes = new(byteArray);

            _logger?.ReturningPong();
            await channel.WriteAsync(bytes);
        }

        _logger?.PingFinished();
    }
}
