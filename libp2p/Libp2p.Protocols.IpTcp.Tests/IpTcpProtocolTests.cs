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
