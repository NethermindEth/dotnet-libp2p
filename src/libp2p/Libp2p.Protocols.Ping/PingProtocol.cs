// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
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
        if (context.State.RemoteAddress is null)
        {
            throw new Libp2pException();
        }

        byte[] ping = new byte[PayloadLength];
        _random.NextBytes(ping.AsSpan(0, PayloadLength));
        ReadOnlySequence<byte> bytes = new(ping);

        _logger?.LogPing(context.State.RemoteAddress);
        await channel.WriteAsync(bytes);
        _logger?.LogTrace("Sent ping: {ping}", Convert.ToHexString(ping));

        _logger?.ReadingPong(context.State.RemoteAddress);
        ReadOnlySequence<byte> response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll).OrThrow();
        _logger?.LogTrace("Received pong: {ping}", Convert.ToHexString(ping));

        _logger?.VerifyingPong(context.State.RemoteAddress);
        if (!ping[0..PayloadLength].SequenceEqual(response.ToArray()))
        {
            _logger?.PingFailed(context.State.RemoteAddress);
            throw new ApplicationException();
        }

        _logger?.LogPinged(context.State.RemoteAddress);
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        if (context.State.RemoteAddress is null)
        {
            throw new Libp2pException();
        }

        _logger?.PingListenStarted(context.State.RemoteAddress);

        while (true)
        {
            _logger?.ReadingPing(context.State.RemoteAddress);
            ReadResult read = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAny);
            if (read.Result != IOResult.Ok)
            {
                break;
            }

            byte[] ping = read.Data.ToArray();
            _logger?.LogTrace("Received ping: {ping}", Convert.ToHexString(ping));

            _logger?.ReturningPong(context.State.RemoteAddress);
            await channel.WriteAsync(new ReadOnlySequence<byte>(ping));
            _logger?.LogTrace("Sent pong: {ping}", Convert.ToHexString(ping));
        }

        _logger?.PingFinished(context.State.RemoteAddress);
    }
}
