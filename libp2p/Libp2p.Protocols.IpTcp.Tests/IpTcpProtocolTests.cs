// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Nethermind.Libp2p.Protocols;

namespace Libp2p.Protocols.IpTcp.Tests;

public class IpTcpProtocolTests
{
    [Test]
    public async Task SendAllAsyncSendsEveryByteFromEverySegment()
    {
        using Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();

        using Socket sender = new(SocketType.Stream, ProtocolType.Tcp);
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using Socket receiver = await listener.AcceptAsync();
        sender.SendBufferSize = 1024;

        byte[] first = new byte[4 * 1024 * 1024];
        byte[] second = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(first);
        Random.Shared.NextBytes(second);

        SequenceSegment firstSegment = new(first);
        SequenceSegment secondSegment = firstSegment.Append(second);
        ReadOnlySequence<byte> payload = new(firstSegment, 0, secondSegment, second.Length);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        Task<byte[]> receiveTask = ReceiveAfterDelayAsync(receiver, first.Length + second.Length, timeout.Token);

        Assert.That(await IpTcpProtocol.SendAllAsync(sender, payload), Is.True);

        byte[] received = await receiveTask;
        byte[] expected = [.. first, .. second];
        Assert.That(received, Is.EqualTo(expected));
    }

    [Test]
    public async Task SendAllAsyncRetainsOriginalBytesWhenLaterSegmentChanges()
    {
        using Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();

        using Socket sender = new(SocketType.Stream, ProtocolType.Tcp);
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
        using Socket receiver = await listener.AcceptAsync();
        sender.SendBufferSize = 1024;

        const int segmentLength = 1024 * 1024;
        const int segmentCount = 16;
        SequenceSegment firstSegment = new(new byte[segmentLength]);
        SequenceSegment lastSegment = firstSegment;
        for (int i = 1; i < segmentCount - 1; i++)
        {
            lastSegment = lastSegment.Append(new byte[segmentLength]);
        }

        byte[] lastBytes = new byte[segmentLength];
        Array.Fill(lastBytes, (byte)0x22);
        lastSegment = lastSegment.Append(lastBytes);
        ReadOnlySequence<byte> payload = new(firstSegment, 0, lastSegment, lastBytes.Length);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        ValueTask<bool> send = IpTcpProtocol.SendAllAsync(sender, payload, timeout.Token);

        Array.Fill(lastBytes, (byte)0x33);

        Task<byte[]> received = ReceiveExactlyAsync(receiver, segmentLength * segmentCount, timeout.Token);
        Assert.That(await send, Is.True);
        byte[] expectedLast = new byte[segmentLength];
        Array.Fill(expectedLast, (byte)0x22);
        Assert.That((await received).AsSpan()[(segmentLength * (segmentCount - 1))..].ToArray(), Is.EqualTo(expectedLast));
    }

    private static async Task<byte[]> ReceiveAfterDelayAsync(Socket socket, int length, CancellationToken token)
    {
        await Task.Delay(100, token);
        return await ReceiveExactlyAsync(socket, length, token);
    }

    private static async Task<byte[]> ReceiveExactlyAsync(Socket socket, int length, CancellationToken token)
    {
        byte[] result = new byte[length];
        int offset = 0;
        while (offset < result.Length)
        {
            int received = await socket.ReceiveAsync(result.AsMemory(offset), token);
            if (received is 0)
            {
                Assert.Fail("The peer closed the socket before the complete payload was received.");
            }

            offset += received;
        }

        return result;
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        private SequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            SequenceSegment next = new(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
