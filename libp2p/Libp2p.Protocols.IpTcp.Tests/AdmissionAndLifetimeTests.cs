// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols;
using NSubstitute;

namespace Nethermind.Libp2p.Protocols.IpTcp.Tests;

public class AdmissionAndLifetimeTests
{
    [Test]
    public async Task ListenerKeepsReadinessDiagnosticsWithoutRetainingAcceptEvents()
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        using ActivitySource source = new("tcp-connection-events");
        System.Collections.Concurrent.ConcurrentQueue<Activity> completed = new();
        using ActivityListener tracing = new()
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (activity.OperationName == "TCP connection") completed.Enqueue(activity); }
        };
        ActivitySource.AddActivityListener(tracing);
        using Activity activity = source.StartActivity("tcp-listener")!;
        Listener listener = new(new object(), new PeerResourceLimits()) { Activity = activity };
        ILoggerFactory factory = Substitute.For<ILoggerFactory>();
        ILogger logger = Substitute.For<ILogger>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        Task transport = new IpTcpProtocol(factory).ListenAsync(listener, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
        try
        {
            List<string> expected = [];
            for (int i = 0; i < 3; i++)
            {
                using TcpClient remote = new();
                await remote.ConnectAsync(await listener.Ready.Task, lifetime.Token);
                expected.Add($"connected {remote.Client.LocalEndPoint}");
                Connection connection = await listener.Connections.Reader.ReadAsync(lifetime.Token);
                remote.Client.Shutdown(SocketShutdown.Send);
                await connection.Disposed.Task.WaitAsync(lifetime.Token);
            }
            Assert.That(() => completed.Count, Is.EqualTo(3).After(5000, 10));
            Assert.That(completed.Select(a => a.Events.Count()), Is.All.EqualTo(1));
            Assert.That(completed.Select(a => a.Events.Single().Name), Is.EquivalentTo(expected));
            Assert.That(completed.Select(a => a.ParentId), Is.All.EqualTo(activity.Id));
            Assert.That(activity.GetTagItem("listen-on-addr"), Is.EqualTo(IPAddress.Loopback));
            Assert.That(activity.GetTagItem("listen-on-port"), Is.EqualTo(0));
            Assert.That(activity.Events.Select(e => e.Name), Is.EqualTo(new[] { "ready" }));
            string[] messages = logger.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
                .Select(c => c.GetArguments()[2]!.ToString()!).ToArray();
            Assert.That(messages.Any(m => m.StartsWith("Ready to handle connections at")), Is.True);
            Assert.That(messages.Any(m => m.StartsWith("Connected ")), Is.True);
        }
        finally { lifetime.Cancel(); await transport.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task RemoteFinEndsCleanlyButResetAborts(bool dial, bool reset)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        Listener context = new(new object(), new PeerResourceLimits());
        IpTcpProtocol protocol = new();
        using TcpListener remoteListener = new(IPAddress.Loopback, 0);
        Task transport;
        TcpClient remote;
        if (dial)
        {
            remoteListener.Start();
            transport = protocol.DialAsync(context, ((IPEndPoint)remoteListener.LocalEndpoint).ToMultiaddress(ProtocolType.Tcp), lifetime.Token);
            remote = await remoteListener.AcceptTcpClientAsync(lifetime.Token);
        }
        else
        {
            transport = protocol.ListenAsync(context, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
            remote = new();
            await remote.ConnectAsync(await context.Ready.Task, lifetime.Token);
        }
        try
        {
            Connection connection = await context.Connections.Reader.ReadAsync(lifetime.Token);
            if (reset)
            {
                remote.Client.LingerState = new LingerOption(true, 0);
                // TcpClient.Dispose first shuts down gracefully; dispose the socket directly to send RST.
                remote.Client.Dispose();
            }
            else remote.Client.Shutdown(SocketShutdown.Send);
            remote.Dispose();
            await connection.Disposed.Task.WaitAsync(lifetime.Token);
            Assert.That((await connection.Channel.Reverse.ReadAsync(1)).Result, Is.EqualTo(reset ? IOResult.Aborted : IOResult.Ended));
        }
        finally
        {
            remote.Dispose();
            lifetime.Cancel();
            await transport.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public void AdmissionNormalizesAddressesAndReleasesSourceEntriesExactlyOnce()
    {
        IpTcpProtocol.Admission admission = new(new PeerResourceLimits(2, 1));
        IDisposable first = admission.TryAcquire(IPAddress.Loopback, out _)!;
        Assert.That(admission.TryAcquire(IPAddress.Parse("::ffff:127.0.0.1"), out string scope), Is.Null);
        Assert.That(scope, Is.EqualTo("source"));
        IDisposable second = admission.TryAcquire(IPAddress.IPv6Loopback, out _)!;
        Assert.That(admission.TryAcquire(IPAddress.Parse("127.0.0.2"), out scope), Is.Null);
        Assert.That(scope, Is.EqualTo("total"));
        first.Dispose(); first.Dispose(); second.Dispose();
        Assert.That(admission.Count, Is.Zero);
        Assert.That(admission.Sources, Is.Zero);
    }

    [Test]
    public void NativeIpv6AddressesShareTheirPrefixAllowance()
    {
        IpTcpProtocol.Admission admission = new(new PeerResourceLimits(3, 1));
        using var first = admission.TryAcquire(IPAddress.Parse("2001:db8:1:2::1"), out _);
        Assert.That(first, Is.Not.Null);
        Assert.That(admission.TryAcquire(IPAddress.Parse("2001:db8:1:2:ffff::abcd"), out string scope), Is.Null);
        Assert.That(scope, Is.EqualTo("source"));
        using var otherPrefix = admission.TryAcquire(IPAddress.Parse("2001:db8:1:3::1"), out _);
        Assert.That(otherPrefix, Is.Not.Null);
        first.Dispose();
        using var recovered = admission.TryAcquire(IPAddress.Parse("2001:db8:1:2::2"), out _);
        Assert.That(recovered, Is.Not.Null);
        Assert.That(admission.Sources, Is.EqualTo(2));
    }

    [Test]
    public void LinkLocalPrefixesPreserveTheirInterfaceScope()
    {
        IpTcpProtocol.Admission admission = new(new PeerResourceLimits(2, 1));
        using var first = admission.TryAcquire(new IPAddress(IPAddress.Parse("fe80::1").GetAddressBytes(), 1), out _);
        using var second = admission.TryAcquire(new IPAddress(IPAddress.Parse("fe80::2").GetAddressBytes(), 2), out _);
        Assert.That(first, Is.Not.Null);
        Assert.That(second, Is.Not.Null);
    }

    [Test]
    public void PressureEvictsOnlyAUsefulIdleCandidateAndKeepsItsPermitUntilTeardown()
    {
        PeerResourceLimits limits = new(2, 1);
        IpTcpProtocol.Admission admission = new(limits);
        using var first = admission.TryAcquire(IPAddress.Loopback, out _)!;
        using var second = admission.TryAcquire(IPAddress.IPv6Loopback, out _)!;
        INewConnectionContext busy = Substitute.For<INewConnectionContext>();
        INewConnectionContext idle = Substitute.For<INewConnectionContext>();
        idle.TryDisconnectIfIdle(limits.InboundTcpIdleTimeout).Returns(true);
        first.SetConnection(busy);
        second.SetConnection(idle);
        Assert.That(admission.TryAcquire(IPAddress.Loopback, out _), Is.Null);
        idle.DidNotReceive().TryDisconnectIfIdle(Arg.Any<TimeSpan>());
        Assert.That(admission.TryAcquire(IPAddress.Parse("127.0.0.2"), out _), Is.Null);
        idle.Received(1).TryDisconnectIfIdle(limits.InboundTcpIdleTimeout);
        Assert.That(admission.Count, Is.EqualTo(2), "Eviction requests do not refund unfinished teardown.");
        second.Dispose();
        using var recovered = admission.TryAcquire(IPAddress.Parse("127.0.0.2"), out _);
        Assert.That(recovered, Is.Not.Null);
    }

    [Test]
    public void ConcurrentAdmissionCannotMultiplyTotalOrSourceAllowances()
    {
        IpTcpProtocol.Admission admission = new(new PeerResourceLimits(8, 2));
        System.Collections.Concurrent.ConcurrentBag<IDisposable> permits = [];
        Parallel.For(0, 256, i =>
        {
            if (admission.TryAcquire(IPAddress.Parse($"127.0.0.{i % 4 + 1}"), out _) is { } permit) permits.Add(permit);
        });
        Assert.That(permits, Has.Count.EqualTo(8));
        Assert.That(admission.Count, Is.EqualTo(8));
        foreach (IDisposable permit in permits) permit.Dispose();
        Assert.That(admission.Sources, Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AdmissionRemainsChargedWhileProtocolTeardownIsBlocked(bool disposalFails)
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource teardown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Listener listener = new(new object(), new PeerResourceLimits(1, 1), teardown.Task, disposalFails);
        Task transport = new IpTcpProtocol().ListenAsync(listener, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
        IPEndPoint endpoint = await listener.Ready.Task;
        try
        {
            using TcpClient first = new();
            await first.ConnectAsync(endpoint, lifetime.Token);
            Connection connection = await listener.Connections.Reader.ReadAsync(lifetime.Token);
            first.Client.Shutdown(SocketShutdown.Send);
            await connection.Disposed.Task.WaitAsync(lifetime.Token);
            using TcpClient refused = new();
            await refused.ConnectAsync(endpoint, lifetime.Token);
            Assert.That(await ReadUntilClosed(refused, lifetime.Token), Is.Zero);
            Assert.That(listener.Connections.Reader.TryRead(out _), Is.False);
            lifetime.Cancel();
            Assert.That(transport.IsCompleted, Is.False, "Listener shutdown must also join protocol teardown.");
        }
        finally
        {
            teardown.TrySetResult();
            lifetime.Cancel();
            await transport.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task ListenersShareAdmissionAndDrainAllPumpsOnShutdown()
    {
        using CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(10));
        object scope = new();
        Listener first = new(scope, new PeerResourceLimits(1, 1));
        Listener second = new(scope, new PeerResourceLimits(1, 1));
        TrackingReceivePool pool = new();
        IpTcpProtocol protocol = new(null, pool);
        Task firstTask = protocol.ListenAsync(first, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
        Task secondTask = protocol.ListenAsync(second, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
        using TcpClient admitted = new();
        await admitted.ConnectAsync(await first.Ready.Task, lifetime.Token);
        Connection connection = await first.Connections.Reader.ReadAsync(lifetime.Token);
        await pool.WaitForRentAsync(lifetime.Token);
        using TcpClient rejected = new();
        await rejected.ConnectAsync(await second.Ready.Task, lifetime.Token);
        Assert.That(await ReadUntilClosed(rejected, lifetime.Token), Is.Zero);
        Assert.That(second.Connections.Reader.TryRead(out _), Is.False, "Refuse before creating a context or renting receive storage.");
        Assert.That(pool.Rentals, Has.Length.EqualTo(1));
        lifetime.Cancel();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(connection.Token.IsCancellationRequested, Is.True);
        Assert.That(connection.Disposals, Is.EqualTo(1));
        Assert.That(connection.Channel.GetAwaiter().IsCompleted, Is.True);
        pool.AssertReturned(1);
    }

    [Test]
    public async Task CompletedConnectionsDoNotAccumulateListenerCancellationRegistrations()
    {
        using CancellationTokenSource lifetime = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        Listener listener = new(new object(), new PeerResourceLimits());
        Task transport = new IpTcpProtocol().ListenAsync(listener, "/ip4/127.0.0.1/tcp/0", lifetime.Token);
        IPEndPoint endpoint = await listener.Ready.Task;
#if DEBUG
        int delegates = IpTcpProtocol.TriggerDisconnection.GetInvocationList().Length;
#endif
        int baseline = RegistrationCount(lifetime);
        try
        {
            for (int i = 0; i < 50; i++)
            {
                using TcpClient remote = new();
                await remote.ConnectAsync(endpoint, deadline.Token);
                Connection connection = await listener.Connections.Reader.ReadAsync(deadline.Token);
                remote.Client.Shutdown(SocketShutdown.Send);
                await connection.Disposed.Task.WaitAsync(deadline.Token);
                Assert.That(connection.Disposals, Is.EqualTo(1));
            }
#if DEBUG
            Assert.That(IpTcpProtocol.TriggerDisconnection.GetInvocationList().Length, Is.EqualTo(delegates));
#endif
            Assert.That(RegistrationCount(lifetime), Is.EqualTo(baseline));
        }
        finally
        {
            lifetime.Cancel();
            await transport.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static int RegistrationCount(CancellationTokenSource source)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object? registrations = typeof(CancellationTokenSource).GetField("_registrations", flags)!.GetValue(source);
        object? node = registrations?.GetType().GetField("Callbacks", flags)!.GetValue(registrations);
        int count = 0;
        while (node is not null)
        {
            // Follow the listener's linked lifetime too: counting only the outer token would
            // miss registrations retained by connection scopes beneath that lifetime.
            if (node.GetType().GetField("CallbackState", flags)!.GetValue(node) is CancellationTokenSource linked)
                count += 1 + RegistrationCount(linked);
            node = node.GetType().GetField("Next", flags)!.GetValue(node);
        }
        return count;
    }

    private static async Task<int> ReadUntilClosed(TcpClient client, CancellationToken token)
    {
        try { return await client.GetStream().ReadAsync(new byte[1], token); }
        catch (IOException) { return 0; }
    }

    private sealed class Listener(object scope, PeerResourceLimits limits, Task? teardown = null, bool disposalFails = false) : ITransportContext
    {
        public object ConnectionAdmissionScope => scope;
        public PeerResourceLimits ResourceLimits => limits;
        public ILocalPeer Peer { get; } = Substitute.For<ILocalPeer>();
        public Activity? Activity { get; init; }
        public TaskCompletionSource<IPEndPoint> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Threading.Channels.Channel<Connection> Connections { get; } = System.Threading.Channels.Channel.CreateUnbounded<Connection>();
        public void ListenerReady(Multiaddress address) => Ready.TrySetResult(address.ToEndPoint());
        public INewConnectionContext CreateConnection()
        {
            Connection connection = new(teardown, disposalFails);
            Connections.Writer.TryWrite(connection);
            return connection;
        }
    }

    private sealed class Connection(Task? teardown = null, bool disposalFails = false) : INewConnectionContext
    {
        public Task Completion => teardown ?? Task.CompletedTask;
        public Channel Channel { get; } = new();
        private readonly CancellationTokenSource _closed = new();
        public CancellationToken Token => _closed.Token;
        public string Id => "test";
        public State State { get; } = new();
        public int Disposals;
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            _closed.Cancel();
            Disposed.TrySetResult();
            if (disposalFails) throw new InvalidOperationException("Injected disposal failure");
        }
        public ILocalPeer Peer => throw new NotSupportedException();
        public Activity? Activity => null;
        public IEnumerable<IProtocol> SubProtocols => [];
        public IChannel Upgrade(UpgradeOptions? options = null) => Channel;
        public IChannel Upgrade(IProtocol protocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel channel, UpgradeOptions? options = null) => throw new NotSupportedException();
        public Task Upgrade(IChannel channel, IProtocol protocol, UpgradeOptions? options = null) => throw new NotSupportedException();
        public INewSessionContext UpgradeToSession() => throw new NotSupportedException();
    }
}
