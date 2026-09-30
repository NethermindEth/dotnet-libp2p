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

        // The request starts with the desired download size. EOF ends the upload.
        byte[] header = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(header, bytesToRecv);
        await WriteAsync(channel, new ReadOnlySequence<byte>(header));

        if (bytesToSend > 0)
            await SendBytesAsync(channel, bytesToSend);

        await WriteEofAsync(channel);

        var received = await DrainUntilEofAsync(channel, bytesToRecv);
        if (received != bytesToRecv)
            throw new InvalidDataException($"Expected to receive {bytesToRecv} bytes, got {received}");
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        var readResult = await channel.ReadAsync(sizeof(ulong), ReadBlockingMode.WaitAll).OrThrow();
        if (readResult.Length != sizeof(ulong))
            throw new InvalidDataException($"Header too short: {readResult.Length} bytes");

        Span<byte> header = stackalloc byte[sizeof(ulong)];
        readResult.CopyTo(header);

        var bytesToSendBack = BinaryPrimitives.ReadUInt64BigEndian(header);
        var bytesToDrain = await DrainUntilEofAsync(channel);

        _logger?.LogInformation("Listen: drain {Drain}, send {Send}", bytesToDrain, bytesToSendBack);

        if (bytesToSendBack > 0)
            await SendBytesAsync(channel, bytesToSendBack);

        await WriteEofAsync(channel);
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
            await WriteAsync(channel, new ReadOnlySequence<byte>(SendBuffer.AsMemory(0, chunkSize)));
            remaining -= (ulong)chunkSize;
        }
    }

    private static async Task<ulong> DrainUntilEofAsync(IChannel channel, ulong? expectedBytes = null)
    {
        ulong total = 0;
        await foreach (ReadOnlySequence<byte> chunk in channel.ReadAllAsync())
        {
            var length = (ulong)chunk.Length;
            if (expectedBytes is ulong expected && (total > expected || length > expected - total))
                throw new InvalidDataException($"Received more than {expected} bytes");
            total = checked(total + length);
        }

        return total;
    }

    private static async Task WriteAsync(IChannel channel, ReadOnlySequence<byte> bytes)
    {
        if (await channel.WriteAsync(bytes) != IOResult.Ok)
            throw new IOException("Perf stream write failed");
    }

    private static async Task WriteEofAsync(IChannel channel)
    {
        if (await channel.WriteEofAsync() != IOResult.Ok)
            throw new IOException("Perf stream EOF failed");
    }
}
