// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;

namespace Libp2p.Protocols.IpTcp.Tests;

public class IpTcpProtocolTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task RejectedAcceptedConnectionClosesItsSocketAndStopsTheListener(bool routine, bool failUpgrade)
    {
        Exception rejection = routine ? new ChannelClosedException() : new InvalidOperationException("Connection creation failed.");
        RejectingContext context = new(rejection, failUpgrade);
        using CancellationTokenSource lifetime = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        using Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task listening = new IpTcpProtocol().ListenAsync(context, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
        try
        {
            IPEndPoint endpoint = await context.Ready.Task.WaitAsync(deadline.Token);
            await client.ConnectAsync(endpoint, deadline.Token);
            await context.Rejected.Task.WaitAsync(deadline.Token);
            Exception? failure = await CaptureFailureAsync(listening.WaitAsync(deadline.Token));

            Assert.Multiple(() =>
            {
                Assert.That(failure, routine ? Is.Null : Is.SameAs(rejection));
                Assert.That(IsEstablished(endpoint, (IPEndPoint)client.LocalEndPoint!), Is.False,
                    "The accepted socket must be closed before the listener task completes.");
                Assert.That(lifetime.IsCancellationRequested, Is.False,
                    "A terminal admission rejection must not cancel the caller's listener token.");
                Assert.That(context.ConnectionAttempts, Is.EqualTo(1));
                Assert.That(context.ConnectionDisposed, Is.EqualTo(failUpgrade));
            });

            Assert.That(await client.ReceiveAsync(new byte[1], SocketFlags.None, deadline.Token), Is.Zero);
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Exception? reconnect = await CaptureFailureAsync(probe.ConnectAsync(endpoint, deadline.Token).AsTask());
            Assert.That(reconnect, Is.InstanceOf<SocketException>());
        }
        finally
        {
            lifetime.Cancel();
            await CaptureFailureAsync(listening.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task RejectedOutgoingConnectionClosesItsSocketAndPreservesTheFailure(bool routine, bool failUpgrade)
    {
        Exception rejection = routine ? new ChannelClosedException() : new InvalidOperationException("Connection creation failed.");
        RejectingContext context = new(rejection, failUpgrade);
        using CancellationTokenSource lifetime = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        IPEndPoint endpoint = (IPEndPoint)listener.LocalEndPoint!;
        Task<Socket> accepting = listener.AcceptAsync(deadline.Token).AsTask();
        Task dialing = new IpTcpProtocol().DialAsync(context, $"/ip4/127.0.0.1/tcp/{endpoint.Port}/p2p/{new Identity().PeerId}", lifetime.Token);
        try
        {
            using Socket accepted = await accepting.WaitAsync(deadline.Token);
            await context.Rejected.Task.WaitAsync(deadline.Token);
            Exception? failure = await CaptureFailureAsync(dialing.WaitAsync(deadline.Token));

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.SameAs(rejection));
                Assert.That(IsEstablished((IPEndPoint)accepted.RemoteEndPoint!, endpoint), Is.False,
                    "The outgoing socket must be closed before the rejected dial task completes.");
                Assert.That(lifetime.IsCancellationRequested, Is.False);
                Assert.That(context.ConnectionAttempts, Is.EqualTo(1));
                Assert.That(context.ConnectionDisposed, Is.EqualTo(failUpgrade));
            });
            Assert.That(await accepted.ReceiveAsync(new byte[1], SocketFlags.None, deadline.Token), Is.Zero);
        }
        finally
        {
            lifetime.Cancel();
            await CaptureFailureAsync(dialing.WaitAsync(TimeSpan.FromSeconds(5)));
            if (!accepting.IsCompleted) listener.Dispose();
            await CaptureFailureAsync(accepting);
        }
    }

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

    private static bool IsEstablished(IPEndPoint local, IPEndPoint remote)
        => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(connection =>
            connection.State == TcpState.Established &&
            SameEndpoint(connection.LocalEndPoint, local) && SameEndpoint(connection.RemoteEndPoint, remote));

    private static bool SameEndpoint(IPEndPoint first, IPEndPoint second)
        => first.Port == second.Port && first.Address.MapToIPv6().Equals(second.Address.MapToIPv6());

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task; return null; }
        catch (Exception failure) { return failure; }
    }

    private sealed class RejectingContext(Exception rejection, bool failUpgrade) : ITransportContext
    {
        private readonly Exception _rejection = rejection;
        private int _connectionAttempts;
        public TaskCompletionSource<IPEndPoint> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Rejected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ConnectionAttempts => Volatile.Read(ref _connectionAttempts);
        public bool ConnectionDisposed { get; private set; }
        public ILocalPeer Peer => throw new NotSupportedException();
        public Activity? Activity => null;
        public void ListenerReady(Multiaddress address) => Ready.TrySetResult(address.ToEndPoint());
        public INewConnectionContext CreateConnection()
        {
            Interlocked.Increment(ref _connectionAttempts);
            if (failUpgrade) return new RejectingConnection(this);
            Rejected.TrySetResult();
            throw _rejection;
        }

        private sealed class RejectingConnection(RejectingContext owner) : INewConnectionContext
        {
            public string Id => "rejected-tcp";
            public State State { get; } = new();
            public CancellationToken Token => CancellationToken.None;
            public ILocalPeer Peer => throw new NotSupportedException();
            public Activity? Activity => null;
            public IEnumerable<IProtocol> SubProtocols => [];
            public void Dispose() => owner.ConnectionDisposed = true;
            public INewSessionContext UpgradeToSession() => throw new NotSupportedException();
            public IChannel Upgrade(UpgradeOptions? options = null)
            {
                owner.Rejected.TrySetResult();
                throw owner._rejection;
            }
            public IChannel Upgrade(IProtocol protocol, UpgradeOptions? options = null) => throw new NotSupportedException();
            public Task Upgrade(IChannel channel, UpgradeOptions? options = null) => throw new NotSupportedException();
            public Task Upgrade(IChannel channel, IProtocol protocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        }
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
