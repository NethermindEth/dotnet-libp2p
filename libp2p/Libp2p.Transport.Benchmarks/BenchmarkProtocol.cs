// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Libp2p.Core;

namespace Nethermind.Libp2p.Transport.Benchmarks;

public sealed class BenchmarkProtocol : ISessionProtocol
{
    private static readonly byte[] Payload = CreatePayload();

    public string Id => "/libp2p-transport-benchmark/1.0.0";

    public static ulong BytesToSend { get; set; }
    public static ulong BytesToReceive { get; set; }

    public async Task DialAsync(IChannel channel, ISessionContext context)
    {
        byte[] header = new byte[2 * sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(header, BytesToSend);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(sizeof(ulong)), BytesToReceive);
        await WriteAsync(channel, header);
        await SendAsync(channel, BytesToSend);
        await WriteEofAsync(channel);
        if (await ReceiveAsync(channel) != BytesToReceive)
        {
            throw new InvalidDataException("Unexpected benchmark response length");
        }
    }

    public async Task ListenAsync(IChannel channel, ISessionContext context)
    {
        ReadOnlySequence<byte> header = await channel.ReadAsync(2 * sizeof(ulong)).OrThrow();
        if (header.Length != 2 * sizeof(ulong))
        {
            throw new InvalidDataException("Incomplete benchmark header");
        }

        Span<byte> headerBytes = stackalloc byte[2 * sizeof(ulong)];
        header.CopyTo(headerBytes);
        ulong expectedUpload = BinaryPrimitives.ReadUInt64BigEndian(headerBytes);
        ulong download = BinaryPrimitives.ReadUInt64BigEndian(headerBytes[sizeof(ulong)..]);

        if (await ReceiveAsync(channel) != expectedUpload)
        {
            throw new InvalidDataException("Unexpected benchmark upload length");
        }
        await SendAsync(channel, download);
        await WriteEofAsync(channel);
    }

    private static async Task SendAsync(IChannel channel, ulong length)
    {
        while (length > 0)
        {
            int chunk = (int)Math.Min(length, (ulong)Payload.Length);
            await WriteAsync(channel, Payload.AsMemory(0, chunk));
            length -= (ulong)chunk;
        }
    }

    private static async Task<ulong> ReceiveAsync(IChannel channel)
    {
        ulong total = 0;
        await foreach (ReadOnlySequence<byte> chunk in channel.ReadAllAsync())
        {
            total = checked(total + (ulong)chunk.Length);
        }
        return total;
    }

    private static async Task WriteAsync(IChannel channel, ReadOnlyMemory<byte> bytes)
    {
        if (await channel.WriteAsync(new ReadOnlySequence<byte>(bytes)) != IOResult.Ok)
        {
            throw new IOException("Benchmark write failed");
        }
    }

    private static async Task WriteEofAsync(IChannel channel)
    {
        if (await channel.WriteEofAsync() != IOResult.Ok)
        {
            throw new IOException("Benchmark stream EOF failed");
        }
    }

    private static byte[] CreatePayload()
    {
        byte[] payload = new byte[64 * 1024];
        payload.AsSpan().Fill(0xAB);
        return payload;
    }
}
