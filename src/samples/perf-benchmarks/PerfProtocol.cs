// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace DataTransferBenchmark;

public class Result
{
    public string Type { get; set; } = "final";
    public double TimeSeconds { get; set; }
    public ulong UploadBytes { get; set; }
    public ulong DownloadBytes { get; set; }
}

public class PerfProtocol : ISessionProtocol
{
    private const int BlockSize = 64 * 1024; // 64KB, matching Go's blockSize
    private readonly ILogger? _logger;
    public string Id => "/perf/1.0.0";
    
    // Add explicit protocol setup
    public bool IsInitiator { get; set; }
    
    // Static variables to track actual bytes transferred 
    public static ulong ActualBytesSent { get; set; } = 0;
    public static ulong ActualBytesReceived { get; set; } = 0;


    public PerfProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PerfProtocol>();
    }

    public static ulong? BytesToReceive { get; set; }
    public static ulong? BytesToSend { get; set; }

    private async Task SendBytesAsync(IChannel channel, ulong bytesToSend)
    {
        var buffer = new byte[BlockSize];
        var rand = new Random();
        var lastReportTime = DateTime.UtcNow;
        ulong lastReportWrite = 0;

        while (bytesToSend > 0)
        {
            var now = DateTime.UtcNow;
            if ((now - lastReportTime).TotalSeconds >= 1.0)
            {
                // Report progress as intermediary result
                var result = new Result
                {
                    Type = "intermediary",
                    TimeSeconds = (now - lastReportTime).TotalSeconds,
                    UploadBytes = lastReportWrite,
                    DownloadBytes = 0
                };

                var jsonOutput = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                { 
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                });
                // Removed logging to keep output clean

                lastReportTime = now;
                lastReportWrite = 0;
            }

            var toSend = (int)Math.Min(bytesToSend, (ulong)BlockSize);
            rand.NextBytes(buffer.AsSpan(0, toSend));
            await channel.WriteAsync(new ReadOnlySequence<byte>(buffer.AsMemory(0, toSend)));
            
            bytesToSend -= (ulong)toSend;
            lastReportWrite += (ulong)toSend;
            ActualBytesSent += (ulong)toSend; // Track actual bytes sent
        }

        _logger?.LogInformation("SendBytesAsync: sent {Bytes} bytes", ActualBytesSent);
    }

    private async Task<ulong> DrainStreamAsync(IChannel channel, ulong expectedBytes = 0)
    {
        ulong total = 0;
        var lastReportTime = DateTime.UtcNow;
        ulong lastReportRead = 0;
        int readAttempts = 0;

        while (expectedBytes == 0 || total < expectedBytes)
        {
            readAttempts++;
            
            try
            {
                // Use WaitAny to avoid hanging on small transfers
                var read = await channel.ReadAsync(BlockSize, ReadBlockingMode.WaitAny).OrThrow();
                
                if (read.Length == 0)
                {
                    break;
                }

                var bytesRead = (ulong)read.Length;
                total += bytesRead;
                lastReportRead += bytesRead;
                ActualBytesReceived += bytesRead; // Track actual bytes received

                // If we have expected bytes set, check if we've read enough
                if (expectedBytes > 0 && total >= expectedBytes)
                {
                    break;
                }

                var now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalSeconds >= 1.0)
                {
                    // Report progress as intermediary result
                    var result = new Result
                    {
                        Type = "intermediary", 
                        TimeSeconds = (now - lastReportTime).TotalSeconds,
                        UploadBytes = 0,
                        DownloadBytes = lastReportRead
                    };

                    var jsonOutput = JsonSerializer.Serialize(result, new JsonSerializerOptions 
                    { 
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
                    });
                    // Removed logging to keep output clean

                    lastReportTime = now;
                    lastReportRead = 0;
                }
            }
            catch (Exception)
            {
                // Don't throw - channel close is expected when client finishes sending
                break;
            }
        }

        _logger?.LogInformation("DrainStreamAsync: drained {Total} bytes, expected {Expected}", total, expectedBytes);
        return total;
    }

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        var bytesToSend = BytesToSend ?? (ulong)BlockSize * 100;
        var bytesToRecv = BytesToReceive ?? (ulong)BlockSize * 100;

        // Send bytes to receive (8 bytes uint64 in BigEndian)
        var sizeBuf = new byte[8];
        if (BitConverter.IsLittleEndian)
        {
            var bytes = BitConverter.GetBytes(bytesToRecv).Reverse().ToArray();
            bytes.CopyTo(sizeBuf, 0);
        }
        else
        {
            BitConverter.TryWriteBytes(sizeBuf, bytesToRecv);
        }
        
        await channel.WriteAsync(new ReadOnlySequence<byte>(sizeBuf));

        // Send our bytes
        await SendBytesAsync(channel, bytesToSend);
        
        // Drain their response - we know exactly how many bytes to expect
        var recvd = await DrainStreamAsync(channel, bytesToRecv);
        
        if (recvd != bytesToRecv)
        {
            throw new InvalidOperationException($"Expected to receive {bytesToRecv} bytes, got {recvd}");
        }

        // Send acknowledgment of received bytes
        var ackBuf = new byte[8];
        if (BitConverter.IsLittleEndian)
        {
            var bytes = BitConverter.GetBytes(recvd).Reverse().ToArray();
            bytes.CopyTo(ackBuf, 0);
        }
        else
        {
            BitConverter.TryWriteBytes(ackBuf, recvd);
        }
        await channel.WriteAsync(new ReadOnlySequence<byte>(ackBuf));
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        // Read size they want to receive (8 bytes uint64 in BigEndian)
        var u64Buf = new byte[8];
        var readResult = await channel.ReadAsync(8, ReadBlockingMode.WaitAll).OrThrow();
        var sequence = readResult;
        if (sequence.Length != 8)
            throw new InvalidDataException("Could not read byte count");

        sequence.CopyTo(u64Buf);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(u64Buf);
        var bytesToSend = BitConverter.ToUInt64(u64Buf);

        // Drain client bytes - expect the same amount that we'll send back
        await DrainStreamAsync(channel, bytesToSend);

        // Send our bytes  
        await SendBytesAsync(channel, bytesToSend);
        
        // Read acknowledgment from client
        var ackBuf = new byte[8];
        var ackReadResult = await channel.ReadAsync(8, ReadBlockingMode.WaitAll).OrThrow();
        if (ackReadResult.Length != 8)
            throw new InvalidDataException("Could not read ack");
        ackReadResult.CopyTo(ackBuf);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(ackBuf);
        var ack = BitConverter.ToUInt64(ackBuf);
        if (ack != bytesToSend)
        {
            throw new InvalidOperationException($"Client acknowledged {ack} bytes, expected {bytesToSend}");
        }
        
        // Connection will close naturally when protocol completes
    }
}