// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Libp2p.Protocols.IpTcp.Tests;

[TestFixture]
public class IpTcpSendProbeTests
{
    [Test]
    public async Task OriginalSendCompletesMultiSegmentPayloadWithSmallSendBuffer()
    {
        (Socket sender, Socket receiver) = await ConnectedSocketsAsync();
        using (sender)
        using (receiver)
        using (CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2)))
        {
            byte[] first = Pattern(4 * 1024 * 1024, 17);
            byte[] second = Pattern(4 * 1024 * 1024, 31);
            SequenceSegment firstSegment = new(first);
            SequenceSegment lastSegment = firstSegment.Append(second);
            ReadOnlySequence<byte> payload = new(firstSegment, 0, lastSegment, second.Length);

            Task<byte[]> received = ReceiveAfterDelayAsync(receiver, (int)payload.Length, timeout.Token);
            int sent = await sender.SendAsync(payload.ToArray(), SocketFlags.None).WaitAsync(timeout.Token);
            Assert.That(sent, Is.EqualTo(payload.Length), $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}: positive short send");
            Assert.That(await received, Is.EqualTo(payload.ToArray()));
            TestContext.Out.WriteLine($"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}: full {sent}-byte multi-segment send");
        }
    }

    [Test]
    public async Task OriginalSendCompletesOneThousandWritesOnOneConnection()
    {
        (Socket sender, Socket receiver) = await ConnectedSocketsAsync();
        using (sender)
        using (receiver)
        using (CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3)))
        {
            long total = 0;
            for (int iteration = 0; iteration < 1000; iteration++)
            {
                int length = iteration % 100 == 0 ? 1024 * 1024 : 4096 + iteration % 61 * 1024;
                byte[] expected = Pattern(length, iteration);
                Task<byte[]> received = ReceiveAfterDelayAsync(receiver, length, timeout.Token, iteration % 25 == 0 ? 2 : 0);
                int sent = await sender.SendAsync(expected, SocketFlags.None).WaitAsync(timeout.Token);
                Assert.That(sent, Is.EqualTo(length), $"iteration {iteration}: positive short send on {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
                Assert.That(await received, Is.EqualTo(expected), $"iteration {iteration}: received bytes differ");
                total += sent;
            }

            TestContext.Out.WriteLine($"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}: 1000 complete sends, {total} bytes verified");
        }
    }

    [Test]
    public async Task OriginalIpTcpDialerDeliversLargePayload()
    {
        using Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        Channel channel = new();
        INewConnectionContext connection = Substitute.For<INewConnectionContext>();
        connection.State.Returns(new State());
        connection.Upgrade(Arg.Any<UpgradeOptions>()).Returns(channel);
        ITransportContext context = Substitute.For<ITransportContext>();
        context.CreateConnection().Returns(connection);

        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
        Multiaddress address = $"/ip4/127.0.0.1/tcp/{port}";
        Task dial = new IpTcpProtocol().DialAsync(context, address, timeout.Token);
        using Socket receiver = await listener.AcceptAsync(timeout.Token);
        byte[] payload = Pattern(8 * 1024 * 1024, 73);
        Task<byte[]> received = ReceiveAfterDelayAsync(receiver, payload.Length, timeout.Token);
        Assert.That(await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token), Is.EqualTo(IOResult.Ok));
        Assert.That(await received, Is.EqualTo(payload));
        TestContext.Out.WriteLine($"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}: original IpTcpProtocol delivered {payload.Length} bytes");

        receiver.Shutdown(SocketShutdown.Both);
        await dial.WaitAsync(timeout.Token);
    }

    private static async Task<(Socket sender, Socket receiver)> ConnectedSocketsAsync()
    {
        using Socket listener = new(SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        Socket sender = new(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await sender.ConnectAsync((IPEndPoint)listener.LocalEndPoint!);
            Socket receiver = await listener.AcceptAsync();
            sender.SendBufferSize = 1024;
            receiver.ReceiveBufferSize = 1024;
            return (sender, receiver);
        }
        catch
        {
            sender.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReceiveAfterDelayAsync(Socket socket, int length, CancellationToken token, int delayMs = 100)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, token);
        }

        byte[] result = new byte[length];
        int offset = 0;
        while (offset < length)
        {
            int received = await socket.ReceiveAsync(result.AsMemory(offset), token);
            if (received == 0)
            {
                Assert.Fail($"Socket ended after {offset} of {length} bytes.");
            }

            offset += received;
        }

        return result;
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = (byte)(i * 17 + (i >> 8) * 23 + seed * 31);
        }

        return result;
    }

    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            SequenceSegment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
