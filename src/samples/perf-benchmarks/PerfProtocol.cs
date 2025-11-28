// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace DataTransferBenchmark;

// TODO: Align with perf protocol
public class PerfProtocol : ISessionProtocol
{
    private readonly ILogger? _logger;
    public string Id => "/perf/1.0.0";

    public PerfProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PerfProtocol>();
    }

    public const long TotalLoad = 1024L * 100;
    private Random rand = new();

    public async Task DialAsync(IChannel downChannel, ISessionContext context)
    {

        await downChannel.WriteVarintAsync(TotalLoad);

        _ = Task.Run(async () =>
        {
            byte[] bytes = new byte[1024 * 1024];
            long bytesWritten = 0;

            for (; ; )
            {
                int bytesToWrite = (int)Math.Min(bytes.Length, TotalLoad - bytesWritten);
                if (bytesToWrite == 0)
                {
                    break;
                }
                rand.NextBytes(bytes.AsSpan(0, bytesToWrite));
                ReadOnlySequence<byte> request = new(bytes, 0, bytesToWrite);
                await downChannel.WriteAsync(request);
                bytesWritten += bytesToWrite;
                _logger?.LogDebug($"Sent {request.Length} more bytes");
            }
        });

        long bytesRead = 0;
        for (; ; )
        {
            ReadOnlySequence<byte> read = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
            _logger?.LogDebug($"DIAL READ {read.Length}");
            bytesRead += read.Length;
            if (bytesRead == TotalLoad)
            {
                _logger?.LogInformation($"DIAL DONE");
                return;
            }
        }
    }

    public async Task ListenAsync(IChannel downChannel, ISessionContext context)
    {
        ulong total = await downChannel.ReadVarintUlongAsync();
        ulong bytesRead = 0;
        for (; ; )
        {
            ReadOnlySequence<byte> read = await downChannel.ReadAsync(0, ReadBlockingMode.WaitAny).OrThrow();
            if (read.Length == 0)
            {
                continue;
            }

            _logger?.LogDebug($"Read {read.Length} more bytes");
            await downChannel.WriteAsync(read).OrThrow();
            _logger?.LogDebug($"Sent back {read.Length}");

            bytesRead += (ulong)read.Length;
            if (bytesRead == total)
            {
                _logger?.LogInformation($"Finished");
                return;
            }
        }
    }
}
