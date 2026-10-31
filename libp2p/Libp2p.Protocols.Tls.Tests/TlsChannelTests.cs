// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using Nethermind.Libp2p.Protocols.Tls;
using NSubstitute;
using Org.BouncyCastle.Tls;
using TlsProtocol = Nethermind.Libp2p.Protocols.Tls.TlsProtocol;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

public class TlsChannelTests
{
    [Test]
    public async Task DisposedUpgradeRetainsItsFailureCategoryAfterAuthentication()
    {
        TestChannel network = new();
        TestChannel clientUp = new();
        TestChannel serverUp = new();
        IConnectionContext clientContext = Context(clientUp, new TaskCompletionSource());
        ObjectDisposedException expected = new("connection");
        clientContext.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ => throw expected);
        IConnectionContext serverContext = Context(serverUp, new TaskCompletionSource(), local: 2, remote: 1);
        Task server = new TlsProtocol().ListenAsync(network, serverContext);
        Task client = new TlsProtocol().DialAsync(network.Reverse(), clientContext);
        try
        {
            Exception? failure = Assert.CatchAsync(async () => await client.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure, Is.SameAs(expected));
        }
        finally
        {
            await clientUp.CloseAsync();
            await serverUp.CloseAsync();
            await network.CloseAsync();
            try { await Task.WhenAll(client, server).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task IncompatibleAlpnFailsBeforeIdentityPublication(bool receiverIsServer)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TestChannel down = new();
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionContext context = Context(new TestChannel(), upgraded, timeout.Token);
        TlsProtocol protocol = new();
        Task run = receiverIsServer ? protocol.ListenAsync(down, context) : protocol.DialAsync(down, context);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(2));
        using SslStream native = new(new ChannelStream(down.Reverse(), leaveOpen: true));
        Task nativeHandshake = receiverIsServer
            ? native.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "libp2p",
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificates = [certificate],
                ApplicationProtocols = [new SslApplicationProtocol("other")],
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, timeout.Token)
            : native.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13,
                ApplicationProtocols = [new SslApplicationProtocol("other")],
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, timeout.Token);
        try
        {
            Assert.CatchAsync(async () => await nativeHandshake.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.ThrowsAsync<AuthenticationException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(context.State.RemotePublicKey, Is.Null);
            Assert.That(upgraded.Task.IsCompleted, Is.False);
        }
        finally
        {
            await down.AbortAsync();
            try { await Task.WhenAll(run, nativeHandshake); }
            catch (Exception ex) when (ex is IOException or AuthenticationException) { }
        }
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task NativeSslStreamInteroperatesInBothRoles(bool receiverIsServer, bool alpn)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        TestChannel down = new();
        TestChannel up = new();
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionContext context = Context(up, upgraded, timeout.Token);
        TlsProtocol protocol = new();
        Task run = receiverIsServer ? protocol.ListenAsync(down, context) : protocol.DialAsync(down, context);
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(2));
        using SslStream native = new(new ChannelStream(down.Reverse(), leaveOpen: true));
        Task? nativeHandshake = null;
        Task? sent = null;
        Task<IOResult>? reply = null;
        Exception? failure = null;
        string phase = "native handshake";
        try
        {
            nativeHandshake = receiverIsServer
                ? native.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "libp2p",
                    EnabledSslProtocols = SslProtocols.Tls13,
                    AllowTlsResume = false,
                    ApplicationProtocols = alpn ? TlsProtocol.ApplicationProtocols : null,
                    ClientCertificates = [certificate],
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
                    RemoteCertificateValidationCallback = (_, remote, _, _) =>
                        remote is X509Certificate2 x509 && CertificateHelper.ValidateCertificate(x509, TestPeers.PeerId(1).ToString()),
                }, timeout.Token)
                : native.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    AllowTlsResume = false,
                    ApplicationProtocols = alpn ? TlsProtocol.ApplicationProtocols : null,
                    RemoteCertificateValidationCallback = (_, remote, _, _) =>
                        remote is X509Certificate2 x509 && CertificateHelper.ValidateCertificate(x509, TestPeers.PeerId(1).ToString()),
                }, timeout.Token);
            await nativeHandshake;

            // Send immediately after Finished, before awaiting the receiver's upgrade.
            byte[] payload = Enumerable.Range(0, 65539).Select(i => (byte)i).ToArray();
            phase = "start native payload write";
            sent = native.WriteAsync(payload, timeout.Token).AsTask();
            phase = "receiver upgrade";
            await upgraded.Task.WaitAsync(timeout.Token);
            phase = "receive native payload";
            ReadResult received = await up.Reverse().ReadAsync(payload.Length, token: timeout.Token);
            Assert.That(received.Result, Is.EqualTo(IOResult.Ok));
            Assert.That(received.Data.ToArray(), Is.EqualTo(payload));
            phase = "complete native payload write";
            await sent;
            phase = "start application reply";
            reply = up.Reverse().WriteAsync(new ReadOnlySequence<byte>(payload), timeout.Token).AsTask();
            byte[] echoed = new byte[payload.Length];
            phase = "receive application reply";
            await native.ReadExactlyAsync(echoed, timeout.Token);
            phase = "complete application reply";
            await reply;
            phase = "authentication assertions";
            Assert.That(echoed, Is.EqualTo(payload));
            Assert.That(native.IsMutuallyAuthenticated, Is.True);
            Assert.That(protocol.LastNegotiatedApplicationProtocol!.Value.Protocol.Length, Is.EqualTo(alpn ? 6 : 0));
            Assert.That(context.State.RemotePublicKey, Is.EqualTo(TestPeers.Identity(2).PublicKey));
        }
        catch (Exception ex)
        {
            failure = ex;
            TestContext.Error.WriteLine($"Native TLS failure during {phase}: server={receiverIsServer}, alpn={alpn}, " +
                $"nativeHandshake={nativeHandshake?.Status}, nativeWrite={sent?.Status}, reply={reply?.Status}, " +
                $"upgrade={upgraded.Task.Status}, receiver={run.Status}, cancelled={timeout.IsCancellationRequested}, " +
                $"OS={Environment.OSVersion}, runtime={Environment.Version}");
            throw;
        }
        finally
        {
            if (failure is not null) timeout.Cancel();
            await up.CloseAsync();
            await down.CloseAsync();
            // A timeout in the exchange must retain its original operation/stack.
            // The receiver commonly observes the same cancellation during cleanup.
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (failure is not null)
            {
                TestContext.Error.WriteLine($"TLS receiver cleanup after {phase}: {ex}");
            }
            foreach (Task? pending in new Task?[] { nativeHandshake, sent, reply })
            {
                if (pending is null) continue;
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (failure is not null)
                {
                    TestContext.Error.WriteLine($"Native TLS I/O cleanup after {phase}: {ex}");
                }
            }
        }
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task HandshakeCancellationStopsActualPendingIo(bool server, bool connectionCancellation)
    {
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource otherLifetime = new();
        TestChannel channel = new();
        TapChannel observed = new(channel);
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionContext context = Context(new TestChannel(), upgraded,
            connectionCancellation ? otherLifetime.Token : cancellation.Token);
        context.Token.Returns(connectionCancellation ? cancellation.Token : otherLifetime.Token);
        TlsProtocol protocol = new();
        Task run = server ? protocol.ListenAsync(observed, context) : protocol.DialAsync(observed, context);
        try
        {
            await Task.WhenAny(observed.ReadEntered.Task, observed.WriteEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Exception? failure = Assert.CatchAsync(async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(context.State.RemotePublicKey, Is.Null);
            Assert.That(upgraded.Task.IsCompleted, Is.False);
            Assert.That(observed.TokenAcquisitions, Is.EqualTo(1));
            Assert.That(channel.GetAwaiter().IsCompleted, Is.True);
        }
        finally
        {
            otherLifetime.Cancel();
            await channel.AbortAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    [TestCase(new byte[] { 22, 3, 3 })]
    [TestCase(new byte[] { 22, 3, 3, 0, 4, 1, 0 })]
    public async Task TruncatedRecordsFailAuthentication(byte[] bytes)
    {
        TestChannel down = new();
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionContext context = Context(new TestChannel(), upgraded);
        Task run = new TlsProtocol().ListenAsync(down, context);
        Assert.That(await down.Reverse().WriteAsync(new ReadOnlySequence<byte>(bytes)), Is.EqualTo(IOResult.Ok));
        await down.Reverse().WriteEofAsync();
        AuthenticationException? failure = Assert.ThrowsAsync<AuthenticationException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(failure!.InnerException, Is.InstanceOf<EndOfStreamException>());
        Assert.That(context.State.RemotePublicKey, Is.Null);
        Assert.That(upgraded.Task.IsCompleted, Is.False);
    }

    [Test]
    public async Task InvalidRecordLengthIsRejectedBeforeReadingItsBody()
    {
        TestChannel down = new();
        TapChannel observed = new(down);
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task run = new TlsProtocol().ListenAsync(observed, Context(new TestChannel(), upgraded));
        await down.Reverse().WriteAsync(new ReadOnlySequence<byte>(new byte[] { 22, 3, 3, 255, 255 }));
        AuthenticationException? failure = Assert.ThrowsAsync<AuthenticationException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(failure!.InnerException, Is.InstanceOf<TlsFatalAlert>());
        Assert.That(observed.ReadCalls, Is.EqualTo(1));
        Assert.That(upgraded.Task.IsCompleted, Is.False);
    }

    [TestCase(new byte[] { 0, 255, 255, 255 })]
    [TestCase(new byte[] { 0, 0, 0, 5, 255, 255, 255, 0, 0 })]
    [TestCase(new byte[] { 0, 0, 0, 6, 0, 0, 1, 42, 255, 255 })]
    [TestCase(new byte[] { 0, 0, 0, 10, 0, 0, 1, 42, 0, 4, 0, 0, 255, 255 })]
    public void CertificateNestedLengthsAreCheckedBeforeAllocation(byte[] body)
    {
        using MemoryStream input = new(body);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<TlsFatalAlert>(() => TlsHandshake.CheckCertificateEncoding(input));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(allocated, Is.LessThan(64 * 1024), "A tiny malformed message must not allocate its declared 16 MiB list.");
        Assert.That(input.Position, Is.Zero);
    }

    [Test]
    public async Task SegmentedBidirectionalDataAndCloseNotifyPreserveOrdering()
    {
        await using Pair pair = await Pair.StartAsync(fragmented: true);
        byte[] first = Enumerable.Range(0, 256 * 1024 + 7).Select(i => (byte)i).ToArray();
        byte[] second = first.Reverse().ToArray();
        Task<IOResult> firstSent = pair.ClientApplication.WriteAsync(Segmented(first)).AsTask();
        Task<IOResult> secondSent = pair.ServerApplication.WriteAsync(Segmented(second)).AsTask();
        Task<ReadResult> atServer = pair.ServerApplication.ReadAsync(first.Length).AsTask();
        Task<ReadResult> atClient = pair.ClientApplication.ReadAsync(second.Length).AsTask();
        await Task.WhenAll(firstSent, secondSent, atServer, atClient).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.That(atServer.Result.Data.ToArray(), Is.EqualTo(first));
        Assert.That(atClient.Result.Data.ToArray(), Is.EqualTo(second));
        Assert.That(firstSent.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(secondSent.Result, Is.EqualTo(IOResult.Ok));
        await pair.ClientApplication.WriteEofAsync();
        ReadResult serverEnd = await pair.ServerApplication.ReadAsync(1, ReadBlockingMode.WaitAny).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await pair.ServerApplication.WriteEofAsync();
        ReadResult clientEnd = await pair.ClientApplication.ReadAsync(1, ReadBlockingMode.WaitAny).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(serverEnd.Result, Is.EqualTo(IOResult.Ended));
        Assert.That(clientEnd.Result, Is.EqualTo(IOResult.Ended));
        await Task.WhenAll(pair.Client, pair.Server).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RepeatedBidirectionalRequestsProgressWithoutUnrelatedTraffic()
    {
        await using Pair pair = await Pair.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        // Each reply is the only event that can release the next request. This
        // exercises reads completing while the owner assembles its I/O wait set.
        async Task Exchange(IChannel application)
        {
            for (int i = 0; i < 1000; i++)
            {
                byte[] payload = BitConverter.GetBytes(i);
                Assert.That(await application.WriteAsync(new ReadOnlySequence<byte>(payload), deadline.Token), Is.EqualTo(IOResult.Ok));
                ReadResult reply = await application.ReadAsync(payload.Length, token: deadline.Token);
                Assert.That(reply.Result, Is.EqualTo(IOResult.Ok));
                Assert.That(reply.Data.ToArray(), Is.EqualTo(payload));
            }
        }
        await Task.WhenAll(Exchange(pair.ClientApplication), Exchange(pair.ServerApplication)).WaitAsync(deadline.Token);
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task PeerTeardownAfterCloseNotifyPreservesEof(bool clientFinishesFirst, bool holdCloseWrite)
    {
        ClosingChannel? held = null;
        await using Pair pair = await Pair.StartAsync(transport: (channel, client) =>
            client != clientFinishesFirst ? held = new ClosingChannel(channel, holdCloseWrite) : channel);
        IChannel first = clientFinishesFirst ? pair.ClientApplication : pair.ServerApplication;
        IChannel last = clientFinishesFirst ? pair.ServerApplication : pair.ClientApplication;
        Task firstPump = clientFinishesFirst ? pair.Client : pair.Server;
        Task lastPump = clientFinishesFirst ? pair.Server : pair.Client;
        held!.Armed = true;
        try
        {
            // Only close_notify can be written after this application EOF. Delay
            // its transport acknowledgement, or the following transport EOF.
            await last.WriteEofAsync();
            Assert.That((await first.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
            await first.WriteEofAsync();
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await firstPump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(lastPump.IsCompleted, Is.False, "The other pump must still own its closing I/O.");
            if (holdCloseWrite)
                await held.ReadHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            held.Release.TrySetResult();
            if (holdCloseWrite)
            {
                await held.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
                held.ReleaseRead.TrySetResult();
            }
            await lastPump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That((await last.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
            Assert.That((await first.ReadAsync(1)).Result, Is.EqualTo(IOResult.Ended));
            Assert.That(held.ClosingResult, Is.EqualTo(IOResult.Aborted));
        }
        finally
        {
            held.Release.TrySetResult();
            held.ReleaseRead.TrySetResult();
        }
    }

    [Test]
    public async Task TransportAbortWithoutPeerCloseNotifyIsNotCleanEof()
    {
        await using Pair pair = await Pair.StartAsync();
        await pair.ClientApplication.WriteEofAsync();
        Assert.That((await pair.ServerApplication.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
        // Our alert arrived, but the peer never sent its own. Transport
        // completion alone must not turn that truncated direction into TLS EOF.
        await pair.ServerDown.AbortAsync();
        Exception? failure = Assert.CatchAsync(async () => await pair.Client.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(failure, Is.InstanceOf<IOException>());
        Assert.That((await pair.ClientApplication.ReadAsync(1)).Result, Is.EqualTo(IOResult.Aborted));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CallerCancellationWinsOverConcurrentTransportAbort(bool withConnectionLifetime)
    {
        using CancellationTokenSource connectionLifetime = new();
        await using Pair pair = await Pair.StartAsync(connectionToken: withConnectionLifetime ? connectionLifetime.Token : default);
        using ManualResetEventSlim release = new();
        // This callback runs before the pump's linked registrations. The source
        // token is cancelled, but the linked tokens cannot observe it yet.
        using CancellationTokenRegistration registration = pair.Cancellation.Token.Register(() =>
        {
            pair.ClientDown.AbortAsync().GetAwaiter().GetResult();
            release.Wait(TimeSpan.FromSeconds(5));
        });
        Task cancel = Task.Run(pair.Cancellation.Cancel);
        try
        {
            foreach (Task pump in new[] { pair.Client, pair.Server })
            {
                Exception? failure = Assert.CatchAsync(async () => await pump.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            }
            Assert.That(cancel.IsCompleted, Is.False, "Both pumps finished before linked cancellation was released.");
        }
        finally
        {
            release.Set();
            await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task PeerWriteEofAllowsReplyAfterReadingEof()
    {
        await using Pair pair = await Pair.StartAsync();
        Task<IOResult> request = pair.ClientApplication.WriteAsync(new ReadOnlySequence<byte>(new byte[] { 42 })).AsTask();
        Assert.That((await pair.ServerApplication.ReadAsync(1)).Data.ToArray(), Is.EqualTo(new byte[] { 42 }));
        await request;
        await pair.ClientApplication.WriteEofAsync();
        Assert.That((await pair.ServerApplication.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
        byte[] response = new byte[65539];
        Task<IOResult> reply = pair.ServerApplication.WriteAsync(new ReadOnlySequence<byte>(response)).AsTask();
        ReadResult received = await pair.ClientApplication.ReadAsync(response.Length).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(received.Result, Is.EqualTo(IOResult.Ok));
        Assert.That(received.Data.ToArray(), Is.EqualTo(response));
        Assert.That(await reply, Is.EqualTo(IOResult.Ok));
        await pair.ServerApplication.WriteEofAsync();
        Assert.That((await pair.ClientApplication.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
        await Task.WhenAll(pair.Client, pair.Server).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ForcedDisposalAfterPeerEofReleasesBlockedReply()
    {
        await using Pair pair = await Pair.StartAsync();
        await pair.ClientApplication.WriteEofAsync();
        Assert.That((await pair.ServerApplication.ReadAsync(1).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Result, Is.EqualTo(IOResult.Ended));
        Task<IOResult> reply = pair.ServerApplication.WriteAsync(new ReadOnlySequence<byte>(new byte[256 * 1024])).AsTask();
        await pair.ClientUp.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(reply.IsCompleted, Is.False);
        await pair.ServerUp.CloseAsync();
        await pair.Server.WaitAsync(TimeSpan.FromSeconds(5));
        Exception? failure = Assert.CatchAsync(async () => await pair.Client.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<OperationCanceledException>());
        Assert.That(await reply.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.EqualTo(IOResult.Ok));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SlowReaderBlocksWritesAndCancellationReleasesThem(bool cancel)
    {
        await using Pair pair = await Pair.StartAsync();
        byte[] payload = new byte[256 * 1024];
        Task<IOResult> sent = pair.ClientApplication.WriteAsync(new ReadOnlySequence<byte>(payload)).AsTask();
        // The peer has decrypted a record and entered a real blocked plaintext
        // write. Its application has not read anything, so the writer is blocked.
        await pair.ServerUp.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(sent.IsCompleted, Is.False);
        Assert.That(pair.ServerUp.MaxWrite, Is.LessThanOrEqualTo(16384));
        if (cancel)
        {
            pair.Cancellation.Cancel();
            Exception? failure = Assert.CatchAsync(async () => await Task.WhenAll(pair.Client, pair.Server).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
            Assert.That(await sent.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.EqualTo(IOResult.Ok));
        }
        else
        {
            await pair.ServerUp.CloseAsync();
            await pair.Server.WaitAsync(TimeSpan.FromSeconds(5));
            Exception? failure = Assert.CatchAsync(async () => await pair.Client.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.That(failure, Is.InstanceOf<OperationCanceledException>().Or.InstanceOf<IOException>());
            Assert.That(await sent.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.EqualTo(IOResult.Ok));
        }
        Assert.That(pair.ClientDown.TokenAcquisitions, Is.EqualTo(1));
        Assert.That(pair.ServerDown.TokenAcquisitions, Is.EqualTo(1));
        Assert.That(pair.ServerUp.TokenAcquisitions, Is.EqualTo(1));
    }

    private static IConnectionContext Context(IChannel up, TaskCompletionSource upgraded, CancellationToken token = default, int local = 1, int remote = 2)
    {
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(TestPeers.Identity(local));
        context.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/1/p2p/{TestPeers.PeerId(remote)}" });
        context.UpgradeOptions.Returns(new UpgradeOptions { CancellationToken = token });
        context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            upgraded.TrySetResult();
            return up;
        });
        return context;
    }

    private static ReadOnlySequence<byte> Segmented(byte[] data)
    {
        Segment first = new(data.AsMemory(0, 17));
        Segment last = first.Append(data.AsMemory(17, 65537)).Append(data.AsMemory(65554));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            Segment next = new(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private sealed class TapChannel(IChannel inner, bool fragment = false) : IChannel
    {
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TokenAcquisitions { get; private set; }
        public int ReadCalls { get; private set; }
        public long MaxWrite { get; private set; }
        public CancellationToken CancellationToken { get { TokenAcquisitions++; return inner.CancellationToken; } }
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode mode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            ReadCalls++;
            ReadEntered.TrySetResult();
            return inner.ReadAsync(fragment ? Math.Min(length, 137) : length, mode, token);
        }
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            MaxWrite = Math.Max(MaxWrite, bytes.Length);
            WriteEntered.TrySetResult();
            return inner.WriteAsync(bytes, token);
        }
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }

    private sealed class ClosingChannel(IChannel inner, bool holdCloseWrite) : IChannel
    {
        public bool Armed { get; set; }
        public IOResult? ClosingResult { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken CancellationToken => inner.CancellationToken;
        public async ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode mode = ReadBlockingMode.WaitAll, CancellationToken token = default)
        {
            ReadResult result = await inner.ReadAsync(length, mode, token);
            if (Armed && holdCloseWrite && length != 5 && result.Result == IOResult.Ok)
            {
                // The transport has consumed the final record body but the TLS
                // owner has not received its continuation. Peer teardown is real.
                ReadHeld.TrySetResult();
                await ReleaseRead.Task.WaitAsync(token);
            }
            return result;
        }
        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            IOResult result = await inner.WriteAsync(bytes, token);
            if (!Armed || !holdCloseWrite) return result;
            Assert.That(result, Is.EqualTo(IOResult.Ok), "The peer consumed our close_notify before tearing down.");
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            // Model a transport reporting teardown after consuming the alert but
            // before acknowledging its write. The real shared channel is closed.
            ClosingResult = await inner.WriteEofAsync(token);
            Acknowledged.TrySetResult();
            return ClosingResult.Value;
        }
        public async ValueTask<IOResult> WriteEofAsync(CancellationToken token = default)
        {
            if (Armed && !holdCloseWrite)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            IOResult result = await inner.WriteEofAsync(token);
            ClosingResult ??= result;
            return result;
        }
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }

    private sealed class Pair : IAsyncDisposable
    {
        private readonly TestChannel _network = new();
        private readonly TestChannel _clientUp = new();
        private readonly TestChannel _serverUp = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public TapChannel ClientDown { get; private set; } = null!;
        public TapChannel ServerDown { get; private set; } = null!;
        public TapChannel ServerUp { get; private set; } = null!;
        public TapChannel ClientUp { get; private set; } = null!;
        public IChannel ClientApplication => _clientUp.Reverse();
        public IChannel ServerApplication => _serverUp.Reverse();
        public Task Client { get; private set; } = null!;
        public Task Server { get; private set; } = null!;
        public static async Task<Pair> StartAsync(bool fragmented = false, Func<IChannel, bool, IChannel>? transport = null,
            CancellationToken connectionToken = default)
        {
            Pair pair = new();
            pair.ClientDown = new(transport?.Invoke(pair._network, true) ?? pair._network, fragmented);
            pair.ServerDown = new(transport?.Invoke(pair._network.Reverse(), false) ?? pair._network.Reverse(), fragmented);
            pair.ServerUp = new(pair._serverUp);
            pair.ClientUp = new(pair._clientUp);
            TaskCompletionSource clientUpgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource serverUpgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IConnectionContext serverContext = Context(pair.ServerUp, serverUpgraded, pair.Cancellation.Token, 2, 1);
            IConnectionContext clientContext = Context(pair.ClientUp, clientUpgraded, pair.Cancellation.Token);
            serverContext.Token.Returns(connectionToken);
            clientContext.Token.Returns(connectionToken);
            pair.Server = new TlsProtocol().ListenAsync(pair.ServerDown, serverContext);
            pair.Client = new TlsProtocol().DialAsync(pair.ClientDown, clientContext);
            try { await Task.WhenAll(clientUpgraded.Task, serverUpgraded.Task).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { await pair.DisposeAsync(); throw; }
            return pair;
        }
        public async ValueTask DisposeAsync()
        {
            await _clientUp.CloseAsync();
            await _serverUp.CloseAsync();
            await _network.CloseAsync();
            try { await Task.WhenAll(Client, Server).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            finally { Cancellation.Dispose(); }
        }
    }
}
