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
        await channel.WriteAsync(new ReadOnlySequence<byte>(bytes));
        ReadOnlySequence<byte> response = await channel.ReadAsync(32, ReadBlockingMode.WaitAll);
        if (!Enumerable.SequenceEqual(bytes[0..32], response.ToArray()))
        {
            _logger?.LogWarning("Wrong response to ping from {from}", context.RemotePeer);
            throw new ApplicationException();
        }
        ArrayPool<byte>.Shared.Return(bytes);
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        while (!channel.IsClosed)
        {
            byte[] request = (await channel.ReadAsync(32, ReadBlockingMode.WaitAll)).ToArray();
            await channel.WriteAsync(new ReadOnlySequence<byte>(request));
        }
    }
}
