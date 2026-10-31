// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NSubstitute;

namespace Libp2p.Protocols.IpTcp.Tests;

public class ReceiveBufferTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task ReceiveSetupSocketErrorUsesTheExistingHandlerAndClosesTheChannel(bool listen)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        using Activity activity = new("receive-setup-failure");
        SocketException failure = new((int)SocketError.NoBufferSpaceAvailable);
        ArrayPool<byte> pool = Substitute.For<ArrayPool<byte>>();
        pool.Rent(Arg.Any<int>()).Returns(_ => throw failure);
        ILogger logger = Substitute.For<ILogger>();
        using ILoggerFactory loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        ReceiveTestChannel channel = new();
        Task<ReadResult> reading = channel.Upper.ReadAsync(1, token: deadline.Token).AsTask();

        await using (TcpPump pump = await TcpPump.StartAsync(listen, new IpTcpProtocol(loggerFactory, pool), channel, deadline.Token, activity))
        {
            await pump.Context.Disposed.Task.WaitAsync(deadline.Token);
            Assert.That((await reading.WaitAsync(deadline.Token)).Result, Is.EqualTo(IOResult.Ended));
            pool.Received(1).Rent(Arg.Is<int>(capacity => capacity > 0));
            pool.DidNotReceive().Return(Arg.Any<byte[]>(), Arg.Any<bool>());
            if (listen)
            {
                Assert.That(activity.Status, Is.EqualTo(ActivityStatusCode.Error));
                Assert.That(activity.Events.Select(item => item.Name), Does.Contain("disconnected due to a socket exception"));
            }
            else
            {
                Assert.That(logger.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
                    .Select(call => call.GetArguments()[2]?.ToString()),
                    Does.Contain($"Ctx(receive-test): end receiving, socket exception {failure.Message}"));
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FragmentedWaitAllRetainsOnlyReceivedBytes(bool listen)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        ReceiveTestChannel channel = new();
        byte[] expected = ReceiveTestChannel.Payload(513);
        ReadOnlySequence<byte> received;
        await using (TcpPump pump = await TcpPump.StartAsync(listen, new IpTcpProtocol(), channel, deadline.Token))
        {
            received = await channel.ReadFragmentsAsync(data => pump.SendAsync(data, deadline.Token), expected,
                Enumerable.Repeat(1, 512).ToArray(), deadline.Token);
            pump.Remote.Shutdown(SocketShutdown.Send);
            await pump.Context.Disposed.Task.WaitAsync(deadline.Token);
        }
        channel.AssertCompactAndStable(expected);
        Assert.That(received.ToArray(), Is.EqualTo(expected));
    }

    [TestCase(false, "eof")]
    [TestCase(true, "eof")]
    [TestCase(false, "rejection")]
    [TestCase(true, "rejection")]
    [TestCase(false, "cancellation")]
    [TestCase(true, "cancellation")]
    [TestCase(false, "failure")]
    [TestCase(true, "failure")]
    public async Task ScratchBufferIsReturnedExactlyOnce(bool listen, string ending)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        TrackingReceivePool pool = new();
        ReceiveTestChannel channel = new()
        {
            Rejection = ending == "rejection" ? IOResult.Ended : null,
            WriteFailure = ending == "failure" ? new InvalidOperationException("Injected write failure") : null,
        };
        await using (TcpPump pump = await TcpPump.StartAsync(listen, new IpTcpProtocol(null, pool), channel, deadline.Token))
        {
            await pool.WaitForRentAsync(deadline.Token);
            switch (ending)
            {
                case "eof": pump.Remote.Shutdown(SocketShutdown.Send); break;
                case "cancellation": pump.Context.CancelConnection(); break;
                default:
                    await pump.SendAsync(new byte[] { 42 }, deadline.Token);
                    await channel.WriteStarted.Task.WaitAsync(deadline.Token);
                    break;
            }
            await pool.WaitForReturnAsync(deadline.Token);
        }
        pool.AssertReturned(1);
        channel.AssertCompactAndStable(ending is "rejection" or "failure" ? [42] : [], pool);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentPumpsPreserveMixedAndOversizedFragments(bool listen)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TrackingReceivePool pool = new();
        IpTcpProtocol protocol = new(null, pool);
        ReceiveTestChannel firstChannel = new();
        ReceiveTestChannel secondChannel = new();
        byte[] firstExpected;
        byte[] secondExpected;
        await using (TcpPump first = await TcpPump.StartAsync(listen, protocol, firstChannel, deadline.Token))
        await using (TcpPump second = await TcpPump.StartAsync(listen, protocol, secondChannel, deadline.Token))
        {
            int capacity = (await pool.WaitForRentAsync(deadline.Token)).Capacity;
            Assert.That((await pool.WaitForRentAsync(deadline.Token)).Capacity, Is.EqualTo(capacity));
            int[] lengths = [1, 17, capacity * 2 + 31, 3];
            firstExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 11);
            secondExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 29);
            await Task.WhenAll(
                firstChannel.ReadFragmentsAsync(data => first.SendAsync(data, deadline.Token), firstExpected, lengths, deadline.Token),
                secondChannel.ReadFragmentsAsync(data => second.SendAsync(data, deadline.Token), secondExpected, lengths, deadline.Token));
            first.Remote.Shutdown(SocketShutdown.Send);
            second.Remote.Shutdown(SocketShutdown.Send);
            await pool.WaitForReturnAsync(deadline.Token);
            await pool.WaitForReturnAsync(deadline.Token);
        }
        pool.AssertReturned(2);
        Assert.That(pool.Rentals[0].Buffer, Is.Not.SameAs(pool.Rentals[1].Buffer));
        firstChannel.AssertCompactAndStable(firstExpected, pool);
        secondChannel.AssertCompactAndStable(secondExpected, pool);
    }

    private sealed class TcpPump(Socket remote, ReceiveContext context, Task transport, CancellationTokenSource lifetime) : IAsyncDisposable
    {
        public Socket Remote { get; } = remote;
        public ReceiveContext Context { get; } = context;

        public static async Task<TcpPump> StartAsync(bool listen, IpTcpProtocol protocol, ReceiveTestChannel channel, CancellationToken token, Activity? activity = null)
        {
            CancellationTokenSource lifetime = new();
            ReceiveContext context = new(channel) { Activity = activity };
            Socket? remote = null;
            Task? transport = null;
            try
            {
                if (listen)
                {
                    transport = protocol.ListenAsync(context, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
                    IPEndPoint endpoint = await context.Ready.Task.WaitAsync(token);
                    remote = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await remote.ConnectAsync(endpoint, token);
                }
                else
                {
                    using Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    listener.Listen();
                    IPEndPoint endpoint = (IPEndPoint)listener.LocalEndPoint!;
                    transport = protocol.DialAsync(context, $"/ip4/127.0.0.1/tcp/{endpoint.Port}", lifetime.Token);
                    remote = await listener.AcceptAsync(token);
                }
                remote.NoDelay = true;
                await context.Upgraded.Task.WaitAsync(token);
                return new TcpPump(remote, context, transport, lifetime);
            }
            catch
            {
                remote?.Dispose();
                context.CancelConnection();
                lifetime.Cancel();
                await channel.CloseAsync();
                if (transport is not null) await ObserveTransportAsync(transport);
                context.Lifetime.Dispose();
                lifetime.Dispose();
                throw;
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token)
        {
            while (!data.IsEmpty)
            {
                int length = await Remote.SendAsync(data, SocketFlags.None, token);
                Assert.That(length, Is.Positive);
                data = data[length..];
            }
        }

        public async ValueTask DisposeAsync()
        {
            Context.CancelConnection();
            lifetime.Cancel();
            Remote.Dispose();
            await Context.Channel.CloseAsync();
            await ObserveTransportAsync(transport);
            await Context.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Context.Lifetime.Dispose();
            lifetime.Dispose();
        }

        private static async Task ObserveTransportAsync(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    private sealed class ReceiveContext(ReceiveTestChannel channel) : ITransportContext, INewConnectionContext
    {
        public ReceiveTestChannel Channel { get; } = channel;
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource<IPEndPoint> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Upgraded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void CancelConnection() => Lifetime.Cancel();
        public void Dispose() { CancelConnection(); Disposed.TrySetResult(); }
        public string Id => "receive-test";
        public State State { get; } = new();
        public CancellationToken Token => Lifetime.Token;
        public ILocalPeer Peer => throw new NotSupportedException();
        public Activity? Activity { get; init; }
        public IEnumerable<IProtocol> SubProtocols => [];
        public void ListenerReady(Multiaddress address) => Ready.TrySetResult(address.ToEndPoint());
        public INewConnectionContext CreateConnection() => this;
        public IChannel Upgrade(UpgradeOptions? options = null) { Upgraded.TrySetResult(); return Channel; }
        public INewSessionContext UpgradeToSession() => throw new NotSupportedException();
        public IChannel Upgrade(IProtocol protocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel channel, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel channel, IProtocol protocol, UpgradeOptions? options = null) => throw new NotSupportedException();
    }
}
