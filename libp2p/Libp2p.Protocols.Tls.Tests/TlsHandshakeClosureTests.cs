// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using Nethermind.Libp2p.Protocols.Tls;
using NSubstitute;
using BcProtocol = Org.BouncyCastle.Tls.TlsProtocol;
using TlsProtocol = Nethermind.Libp2p.Protocols.Tls.TlsProtocol;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

public class TlsHandshakeClosureTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task PeerCloseBeforeFinalWriteAcknowledgementWaitsForUpgrade(bool fragmented)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(1));
        using TlsHandshake clientHandshake = new(certificate, false, _ => true);
        using TlsHandshake server = new(certificate, true, _ => true);
        TestChannel down = new();
        TestChannel up = new();
        AcknowledgedChannel probe = new(down, fragmented);
        IChannel peer = down.Reverse();
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Start();
        Task client = TlsChannel.RunAsync(clientHandshake, probe, () =>
        {
            Assert.That(clientHandshake.Complete, Is.True);
            Assert.That(clientHandshake.RemoteCertificate!.RawData, Is.EqualTo(certificate.RawData));
            upgraded.TrySetResult();
            return up;
        }, deadline.Token);
        Task<IOResult>? applicationWrite = null;
        try
        {
            TaskCompletionSource finalAcknowledgement;
            while (true)
            {
                await OfferRecord(server.Engine, peer, deadline.Token);
                TaskCompletionSource acknowledgement = await probe.Acknowledgements.Reader.ReadAsync(deadline.Token);
                if (server.Complete)
                {
                    finalAcknowledgement = acknowledgement;
                    break;
                }
                acknowledgement.TrySetResult();
                if (server.Engine.GetAvailableOutputBytes() > 0)
                    Assert.That(await peer.WriteAsync(new ReadOnlySequence<byte>(Drain(server.Engine)), deadline.Token), Is.EqualTo(IOResult.Ok));
            }
            // The peer verified our Finished, but its transport has not yet
            // acknowledged our last write. It may immediately half-close TLS.
            server.SendCloseNotify();
            Assert.That(await peer.WriteAsync(new ReadOnlySequence<byte>(Drain(server.Engine)), deadline.Token), Is.EqualTo(IOResult.Ok));
            while (!clientHandshake.PeerClosed && !client.IsCompleted)
                await Task.Delay(1, deadline.Token);
            Assert.That(clientHandshake.PeerClosed, Is.True, "Observe the parsed alert, not merely a completed transport read.");
            Assert.That(upgraded.Task.IsCompleted, Is.False, "Our final handshake write is still unacknowledged.");
            finalAcknowledgement.TrySetResult();
            await Task.WhenAny(upgraded.Task, client).WaitAsync(deadline.Token);
            if (client.IsCompleted) await client;
            await upgraded.Task.WaitAsync(deadline.Token);
            Assert.That((await up.Reverse().ReadAsync(1, token: deadline.Token)).Result, Is.EqualTo(IOResult.Ended));

            // A peer write EOF still permits a reply in the opposite direction.
            applicationWrite = up.Reverse().WriteAsync(new ReadOnlySequence<byte>(new byte[] { 42 }), deadline.Token).AsTask();
            await OfferRecord(server.Engine, peer, deadline.Token);
            (await probe.Acknowledgements.Reader.ReadAsync(deadline.Token)).TrySetResult();
            byte[] reply = new byte[1];
            Assert.That(server.Engine.ReadInput(reply, 0, 1), Is.EqualTo(1));
            Assert.That(reply, Is.EqualTo(new byte[] { 42 }));
            Assert.That(await applicationWrite, Is.EqualTo(IOResult.Ok));
            await up.Reverse().WriteEofAsync(deadline.Token);
            await OfferRecord(server.Engine, peer, deadline.Token);
            Assert.That(server.PeerClosed, Is.True);
            (await probe.Acknowledgements.Reader.ReadAsync(deadline.Token)).TrySetResult();
            await client.WaitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            await down.AbortAsync();
            await up.AbortAsync();
            foreach (Task? task in new Task?[] { client, applicationWrite })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { /* Preserve the primary assertion or protocol failure. */ }
            }
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task InternalFaultBeforeUpgradeRetainsItsCategory(bool server)
    {
        IChannel failed = Substitute.For<IChannel>();
        failed.CancellationToken.Returns(CancellationToken.None);
        InvalidOperationException expected = new("Injected channel implementation fault");
        failed.ReadAsync(Arg.Any<int>(), Arg.Any<ReadBlockingMode>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<ReadResult>(expected));
        failed.WriteAsync(Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<CancellationToken>()).Returns(IOResult.Ok);
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(TestPeers.Identity(1));
        context.State.Returns(new State());
        Task run = server ? new TlsProtocol().ListenAsync(failed, context) : new TlsProtocol().DialAsync(failed, context);
        Exception? failure = Assert.CatchAsync(async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.That(failure, Is.SameAs(expected));
        Assert.That(context.State.RemotePublicKey, Is.Null);
        _ = context.DidNotReceive().Upgrade(Arg.Any<UpgradeOptions>());
    }

    private static byte[] Drain(BcProtocol engine)
    {
        byte[] bytes = new byte[engine.GetAvailableOutputBytes()];
        Assert.That(engine.ReadOutput(bytes, 0, bytes.Length), Is.EqualTo(bytes.Length));
        return bytes;
    }

    private static async Task OfferRecord(BcProtocol engine, IChannel channel, CancellationToken token)
    {
        ReadResult header = await channel.ReadAsync(5, token: token);
        Assert.That(header.Result, Is.EqualTo(IOResult.Ok));
        byte[] record = new byte[engine.PreviewInputRecord(header.Data.ToArray()).RecordSize];
        header.Data.CopyTo(record);
        ReadResult body = await channel.ReadAsync(record.Length - 5, token: token);
        Assert.That(body.Result, Is.EqualTo(IOResult.Ok));
        body.Data.CopyTo(record.AsSpan(5));
        engine.OfferInput(record);
    }

    private sealed class AcknowledgedChannel(IChannel inner, bool fragmented) : IChannel
    {
        public System.Threading.Channels.Channel<TaskCompletionSource> Acknowledgements { get; } =
            System.Threading.Channels.Channel.CreateBounded<TaskCompletionSource>(1);
        public CancellationToken CancellationToken => inner.CancellationToken;
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode mode = ReadBlockingMode.WaitAll, CancellationToken token = default) =>
            inner.ReadAsync(fragmented ? Math.Min(length, 3) : length, mode, token);
        public async ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            IOResult result = await inner.WriteAsync(bytes, token);
            if (result != IOResult.Ok) return result;
            TaskCompletionSource acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await Acknowledgements.Writer.WriteAsync(acknowledgement, token);
            await acknowledgement.Task.WaitAsync(token);
            return result;
        }
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }
}
