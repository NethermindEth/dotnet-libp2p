// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;

namespace DataTransferBenchmark;

public class PerfProtocol : ISessionProtocol
{
    private const int BlockSize = 64 * 1024; // 64KB, matching Go's blockSize
    private const int MaxConsecutiveFailures = 3;

    private static readonly byte[] SendBuffer;

    private readonly ILogger? _logger;

    public string Id => "/perf/1.0.0";

    public static ulong BytesToReceive { get; set; }
    public static ulong BytesToSend { get; set; }

    static PerfProtocol()
    {
        // Pre-allocate a reusable send buffer once.
        // Fill with a non-zero pattern to avoid OS zero-page deduplication.
        SendBuffer = new byte[BlockSize];
        SendBuffer.AsSpan().Fill(0xAB);
    }

    public PerfProtocol(ILoggerFactory? loggerFactory = null)
    {
        _logger = loggerFactory?.CreateLogger<PerfProtocol>();
    }

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        var bytesToSend = BytesToSend;
        var bytesToRecv = BytesToReceive;

        // Send 16-byte header (matching Rust protocol):
        //   [8B BE] bytesToSend — how many bytes we will upload
        //   [8B BE] bytesToRecv — how many bytes we want the server to send back
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(header[..8], bytesToSend);
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], bytesToRecv);
        await channel.WriteAsync(new ReadOnlySequence<byte>(header.ToArray()));

        if (bytesToSend > 0)
            await SendBytesAsync(channel, bytesToSend);

        if (bytesToRecv > 0)
        {
            var recvd = await DrainBytesAsync(channel, bytesToRecv);
            if (recvd != bytesToRecv)
                throw new InvalidOperationException(
                    $"Expected to receive {bytesToRecv} bytes, got {recvd}");
        }
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        // Read 16-byte header (matching Rust protocol)
        var readResult = await channel.ReadAsync(16, ReadBlockingMode.WaitAll).OrThrow();
        if (readResult.Length != 16)
            throw new InvalidDataException($"Header too short: {readResult.Length} bytes");

        Span<byte> header = stackalloc byte[16];
        readResult.CopyTo(header);

        // First 8B: bytes client will send, Next 8B: bytes client wants back
        var bytesToDrain = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
        var bytesToSendBack = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);

        _logger?.LogInformation("Listen: drain {Drain}, send {Send}", bytesToDrain, bytesToSendBack);

        if (bytesToDrain > 0)
            await DrainBytesAsync(channel, bytesToDrain);

        if (bytesToSendBack > 0)
            await SendBytesAsync(channel, bytesToSendBack);
    }

    /// <summary>
    /// Writes <paramref name="totalBytes"/> of payload to the channel using a
    /// pre-filled static buffer (no per-chunk allocation or random fill).
    /// </summary>
    private static async Task SendBytesAsync(IChannel channel, ulong totalBytes)
    {
        var remaining = totalBytes;
        while (remaining > 0)
        {
            var chunkSize = (int)Math.Min(remaining, (ulong)BlockSize);
            await channel.WriteAsync(
                new ReadOnlySequence<byte>(SendBuffer.AsMemory(0, chunkSize)));
            remaining -= (ulong)chunkSize;
        }
        // Note: No explicit flush needed - both sides use exact byte counts
    }

    /// <summary>
    /// Reads exactly <paramref name="expectedBytes"/> from the channel.
    /// Uses WaitAll mode and caps read requests to the remaining byte count.
    /// </summary>
    private async Task<ulong> DrainBytesAsync(IChannel channel, ulong expectedBytes)
    {
        ulong total = 0;
        int consecutiveErrors = 0;

        while (total < expectedBytes)
        {
            var remaining = expectedBytes - total;
            var requestSize = (int)Math.Min(remaining, (ulong)BlockSize);

            try
            {
                var chunk = await channel.ReadAsync(requestSize, ReadBlockingMode.WaitAll).OrThrow();

                if (chunk.Length == 0)
                {
                    if (++consecutiveErrors >= MaxConsecutiveFailures) break;
                    await Task.Delay(10);
                    continue;
                }

                consecutiveErrors = 0;
                total += (ulong)chunk.Length;
            }
            catch (Exception ex)
            {
                if (++consecutiveErrors >= MaxConsecutiveFailures)
                {
                    _logger?.LogError("Drain failed at {Total}/{Expected}: {Msg}",
                        total, expectedBytes, ex.Message);
                    break;
                }
                await Task.Delay(10);
            }
        }

        return total;
    }
}