// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace DataTransferBenchmark;

// TODO: Align with perf protocol
public class PerfProtocol : IProtocol
{
    private readonly ILogger? _logger;
    public string Id => "/perf/1.0.0";

    public PerfProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PerfProtocol>();
    }

    public const long TotalLoad = 1024L * 1024 * 100;
    private Random rand = new();

    public async Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {

        await downChannel.WriteVarintAsync(TotalLoad);

        _ = Task.Run(async () =>
        {
            byte[] bytes = new byte[1024 * 1024];
            long bytesWritten = 0;

            while (!downChannel.Token.IsCancellationRequested)
            {
                int bytesToWrite = (int)Math.Min(bytes.Length, TotalLoad - bytesWritten);
                if (bytesToWrite == 0)
                {
                    break;
                }
                rand.NextBytes(bytes.AsSpan(0, bytesToWrite));
                ReadOnlySequence<byte> bytesToSend = new(bytes, 0, bytesToWrite);
                bytesWritten += bytesToWrite;
                await downChannel.WriteAsync(bytesToSend);
                _logger?.LogDebug($"DIAL WRIT {bytesToSend.Length}");
            }
        });

        long bytesRead = 0;
        while (!downChannel.Token.IsCancellationRequested)
        {
            ReadOnlySequence<byte> read =
                await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny, downChannel.Token);
            _logger?.LogDebug($"DIAL READ {read.Length}");
            bytesRead += read.Length;
            if (bytesRead == TotalLoad)
            {
                _logger?.LogInformation($"DIAL DONE");
                return;
            }
        }
    }

    public async Task ListenAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {
        ulong total = await downChannel.ReadVarintUlongAsync();
        ulong bytesRead = 0;
        while (!downChannel.Token.IsCancellationRequested)
        {
            ReadOnlySequence<byte> read =
                await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny, downChannel.Token);
            if (read.Length == 0)
            {
                continue;
            }

            _logger?.LogDebug($"LIST READ {read.Length}");
            await downChannel.WriteAsync(read);
            _logger?.LogDebug($"LIST WRITE {read.Length}");

            bytesRead += (ulong)read.Length;
            if (bytesRead == total)
            {
                _logger?.LogInformation($"LIST DONE");
                return;
            }
        }
    }
}
