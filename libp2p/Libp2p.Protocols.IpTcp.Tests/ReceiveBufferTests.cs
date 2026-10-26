// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;

namespace Libp2p.Protocols.IpTcp.Tests;

public class ReceiveBufferTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task FragmentedWaitAllPacksStorageAndSurvivesLaterReads(bool listen)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        ReceiveTestChannel channel = new();
        byte[] expected = ReceiveTestChannel.Payload(513);
        byte[] later = ReceiveTestChannel.Payload(131073, 29);
        ReadOnlySequence<byte> retained;
        await using (TcpPump pump = await TcpPump.StartAsync(listen, new IpTcpProtocol(), channel, deadline.Token))
        {
            retained = await channel.ReadFragmentsAsync(data => pump.SendAsync(data, deadline.Token), expected,
                Enumerable.Repeat(1, 512).ToArray(), deadline.Token);
            channel.AssertCompactAndStable(expected);
            await channel.ReadFragmentsAsync(data => pump.SendAsync(data, deadline.Token), later, [1, 17, later.Length - 19], deadline.Token);
            Assert.That(retained.ToArray(), Is.EqualTo(expected));
            pump.Remote.Shutdown(SocketShutdown.Send);
            await pump.Context.Disposed.Task.WaitAsync(deadline.Token);
        }
        channel.AssertCompactAndStable([.. expected, .. later]);
        Assert.That(retained.ToArray(), Is.EqualTo(expected));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentPumpsPreserveMixedAndOversizedFragments(bool listen)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        IpTcpProtocol protocol = new();
        ReceiveTestChannel firstChannel = new();
        ReceiveTestChannel secondChannel = new();
        int[] lengths = [1, 17, 131103, 3];
        byte[] firstExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 11);
        byte[] secondExpected = ReceiveTestChannel.Payload(lengths.Sum() + 1, 29);
        await using (TcpPump first = await TcpPump.StartAsync(listen, protocol, firstChannel, deadline.Token))
        await using (TcpPump second = await TcpPump.StartAsync(listen, protocol, secondChannel, deadline.Token))
        {
            await Task.WhenAll(
                firstChannel.ReadFragmentsAsync(data => first.SendAsync(data, deadline.Token), firstExpected, lengths, deadline.Token),
                secondChannel.ReadFragmentsAsync(data => second.SendAsync(data, deadline.Token), secondExpected, lengths, deadline.Token));
            first.Remote.Shutdown(SocketShutdown.Send);
            second.Remote.Shutdown(SocketShutdown.Send);
            await Task.WhenAll(first.Context.Disposed.Task, second.Context.Disposed.Task).WaitAsync(deadline.Token);
        }
        firstChannel.AssertCompactAndStable(firstExpected);
        secondChannel.AssertCompactAndStable(secondExpected);
    }

    private sealed class TcpPump(Socket remote, ReceiveContext context, Task transport, CancellationTokenSource lifetime) : IAsyncDisposable
    {
        public Socket Remote { get; } = remote;
        public ReceiveContext Context { get; } = context;

        public static async Task<TcpPump> StartAsync(bool listen, IpTcpProtocol protocol, ReceiveTestChannel channel, CancellationToken token)
        {
            CancellationTokenSource lifetime = new();
            ReceiveContext context = new(channel);
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
        public Activity? Activity => null;
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
