// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Ping;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/ping/ping.md
/// </summary>
public class PingProtocol : ISessionProtocol
{
    private const int PayloadLength = 32;

    public string Id => "/ipfs/ping/1.0.0";
    private readonly Random _random = new();
    private readonly ILogger<PingProtocol>? _logger;

    public PingProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PingProtocol>();
    }

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        using PooledBuffer ping = PooledBuffer.Rent(PayloadLength);
        _random.NextBytes(ping.Span);

        _logger?.LogPing(context.State.RemoteAddress);
        await channel.WriteAsync(ping, PayloadLength);
        _logger?.LogTrace("Sent ping: {ping}", Convert.ToHexString(ping.ReadOnlySpan));

        _logger?.ReadingPong(context.State.RemoteAddress);
        using ReadResult response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll).OrThrow();
        _logger?.LogTrace("Received pong: {ping}", Convert.ToHexString(response.Data));

        _logger?.VerifyingPong(context.State.RemoteAddress);
        if (!ping.ReadOnlySpan.SequenceEqual(response.Data))
        {
            _logger?.PingFailed(context.State.RemoteAddress);
            throw new ApplicationException();
        }

        _logger?.LogPinged(context.State.RemoteAddress);
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context.State.RemoteAddress);

        _logger?.PingListenStarted(context.State.RemoteAddress);

        while (true)
        {
            _logger?.ReadingPing(context.State.RemoteAddress);
            ReadResult read = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAny);
            if (read.Result != IOResult.Ok)
            {
                read.Dispose();
                break;
            }

            _logger?.LogTrace("Received ping: {ping}", Convert.ToHexString(read.Data));

            _logger?.ReturningPong(context.State.RemoteAddress);
            using PooledBuffer.Slice ping = read.ToSlice();
            await channel.WriteAsync(ping);
            _logger?.LogTrace("Sent pong: {ping}", Convert.ToHexString(read.Data));
            read.Dispose();
        }

        _logger?.PingFinished(context.State.RemoteAddress);
    }
}
