// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Org.BouncyCastle.Crypto.Signers;

public class DataTransferBenchmarkProtocol : IProtocol
{
    private readonly ILogger? _logger;
    public string Id => "/data-transfer-benchmark/1.0.0";

    public DataTransferBenchmarkProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<DataTransferBenchmarkProtocol>();
    }
    
    public const int TotalLoad = 1024 * 1024 * 1024;
    private Random rand = new ();
    
    public async Task DialAsync(IChannel downChannel, IChannelFactory upChannelFactory, IPeerContext context)
    {    

        await downChannel.WriteVarintAsync(TotalLoad);
        
        _ = Task.Run(async () => {
            byte[] bytes = new byte[1024 * 1024];
            long bytesWritten = 0;

            while (!downChannel.Token.IsCancellationRequested)
            {
                var bytesToWrite = (int)Math.Min(bytes.Length, TotalLoad - bytesWritten);
                if (bytesToWrite == 0)
                {
                    break;
                }
                rand.NextBytes(bytes.AsSpan(0, bytesToWrite));
                var bytesToSend = new ReadOnlySequence<byte>(bytes, 0, bytesToWrite);
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
        int total = await downChannel.ReadVarintAsync();
        long bytesRead = 0;
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

            bytesRead += read.Length;
            if (bytesRead == total)
            {
                _logger?.LogInformation($"LIST DONE");
                return;
            }
        }
    }
}
