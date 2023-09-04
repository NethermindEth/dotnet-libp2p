// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using System.Buffers;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/ping/ping.md
/// </summary>
public class PingProtocol : IProtocol
{
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
        byte[] bytes = ArrayPool<byte>.Shared.Rent(32);
        _random.NextBytes(bytes.AsSpan(0, 32));
        _logger?.LogDebug("Ping {remotePeer}", context.RemotePeer.Address);
        await channel.WriteAsync(new ReadOnlySequence<byte>(bytes));
        _logger?.LogTrace("Reading pong");
        ReadOnlySequence<byte> response = await channel.ReadAsync(32, ReadBlockingMode.WaitAll);
        _logger?.LogTrace("Verifing pong");
        if (!Enumerable.SequenceEqual(bytes[0..32], response.ToArray()))
        {
            _logger?.LogWarning("Wrong response to ping from {from}", context.RemotePeer);
            throw new ApplicationException();
        }
        ArrayPool<byte>.Shared.Return(bytes);
        _logger?.LogDebug("Pinged");
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        _logger?.LogDebug("Ping listen started from {remotePeer}", context.RemotePeer);

        while (!channel.IsClosed)
        {
            _logger?.LogTrace("Reading ping");
            byte[] request = (await channel.ReadAsync(32, ReadBlockingMode.WaitAll)).ToArray();
            _logger?.LogTrace("Returning back");
            await channel.WriteAsync(new ReadOnlySequence<byte>(request));
        }
        _logger?.LogDebug("Ping finished");
    }
}
