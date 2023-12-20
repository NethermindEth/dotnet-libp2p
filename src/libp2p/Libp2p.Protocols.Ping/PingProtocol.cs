// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Ping;

namespace Nethermind.Libp2p.Protocols;

/// <summary>
///     https://github.com/libp2p/specs/blob/master/ping/ping.md
/// </summary>
public class PingProtocol : IId, IDialer<long>, IListener
{
    private const int PayloadLength = 32;

    public string Id => "/ipfs/ping/1.0.0";
    private readonly Random _random = new();
    private readonly ILogger<PingProtocol>? _logger;

    public PingProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PingProtocol>();
    }

    public async Task<long> DialAsync(IChannel channel, IChannelFactory? channelFactory, IPeerContext context)
    {
        byte[] byteArray = new byte[PayloadLength];
        _random.NextBytes(byteArray.AsSpan(0, PayloadLength));
        ReadOnlySequence<byte> bytes = new(byteArray);

        _logger?.LogPing(context.RemotePeer.Address);

        Stopwatch pingDelay = Stopwatch.StartNew();
        await channel.WriteAsync(bytes);
        ReadOnlySequence<byte> response = await channel.ReadAsync(PayloadLength, ReadBlockingMode.WaitAll);
        pingDelay.Stop();

        _logger?.ReadPong(context.RemotePeer.Address);

        _logger?.VerifyingPong(context.RemotePeer.Address);
        if (!byteArray[0..PayloadLength].SequenceEqual(response.ToArray()))
        {
            _logger?.PingFailed(context.RemotePeer.Address);
            throw new ApplicationException();
        }

        _logger?.LogPinged(context.RemotePeer.Address);
        return pingDelay.ElapsedMilliseconds;
    }

    public async Task ListenAsync(IChannel channel, IChannelFactory? channelFactory,
        IPeerContext context)
    {
        _logger?.PingListenStarted(context.RemotePeer.Address);

        while (!channel.IsClosed)
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
