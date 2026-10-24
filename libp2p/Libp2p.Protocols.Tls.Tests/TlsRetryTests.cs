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
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using BcProtocol = Org.BouncyCastle.Tls.TlsProtocol;
using TlsProtocol = Nethermind.Libp2p.Protocols.Tls.TlsProtocol;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

public class TlsRetryTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task CoalescedRetryFlightProgressesWhileClientOutputIsBlocked(bool fragmented, bool cancel)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TestChannel down = new();
        TestChannel up = new();
        ClientProbe channel = new(down, fragmented);
        TaskCompletionSource upgraded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IConnectionContext context = Substitute.For<IConnectionContext>();
        context.Peer.Identity.Returns(TestPeers.Identity(1));
        context.State.Returns(new State { RemoteAddress = $"/ip4/127.0.0.1/tcp/1/p2p/{TestPeers.PeerId(2)}" });
        context.UpgradeOptions.Returns(new UpgradeOptions { CancellationToken = deadline.Token });
        context.Upgrade(Arg.Any<UpgradeOptions>()).Returns(_ =>
        {
            upgraded.TrySetResult();
            return up;
        });
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 certificate = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(2));
        RetryEngine server = new();
        RetryServer authentication = new(certificate);
        server.Accept(authentication);
        IChannel peer = down.Reverse();
        Task client = new TlsProtocol().DialAsync(channel, context);
        Task<IOResult>? flightWrite = null;
        Task<IOResult>? applicationWrite = null;
        try
        {
            await OfferRecordAsync(server, peer, deadline.Token);
            Assert.That(server.Retries, Is.EqualTo(1), "The production client must actually receive HelloRetryRequest.");
            byte[] flight = DrainOutput(server);
            int firstRecordLength = 5 + (flight[3] << 8) + flight[4];
            Assert.That(flight[firstRecordLength..], Is.EqualTo(new byte[] { 20, 3, 3, 0, 1, 1 }),
                "The single peer write contains the retry followed by compatibility CCS.");
            flightWrite = peer.WriteAsync(new ReadOnlySequence<byte>(flight), deadline.Token).AsTask();
            await channel.CompatibilityWriteEntered.Task.WaitAsync(deadline.Token);
            Assert.That(channel.CompatibilityWrite!.IsCompleted, Is.False,
                "The peer has not read any client retry output yet.");
            Assert.That(upgraded.Task.IsCompleted, Is.False);
            Assert.That(context.State.RemotePublicKey, Is.Null);

            // No client reads are started until this entire coalesced flight drains.
            // The client must consume the peer's CCS while its own CCS write is
            // blocked and its second ClientHello remains queued in the engine.
            Assert.That(await flightWrite.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(IOResult.Ok));
            if (cancel)
            {
                Assert.That(channel.CompatibilityWrite.IsCompleted, Is.False);
                deadline.Cancel();
                Exception? failure = Assert.CatchAsync(async () => await client.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
                Assert.That(await channel.CompatibilityWrite.WaitAsync(TimeSpan.FromSeconds(5)), Is.Not.EqualTo(IOResult.Ok));
                Assert.That(upgraded.Task.IsCompleted, Is.False);
                Assert.That(context.State.RemotePublicKey, Is.Null);
                return;
            }
            while (!authentication.Complete)
            {
                await OfferRecordAsync(server, peer, deadline.Token);
                if (server.GetAvailableOutputBytes() > 0)
                {
                    flightWrite = peer.WriteAsync(new ReadOnlySequence<byte>(DrainOutput(server)), deadline.Token).AsTask();
                    Assert.That(await flightWrite, Is.EqualTo(IOResult.Ok));
                }
            }
            await upgraded.Task.WaitAsync(deadline.Token);
            Assert.That(authentication.ClientAuthenticated, Is.True);
            Assert.That(context.State.RemotePublicKey, Is.EqualTo(TestPeers.Identity(2).PublicKey));

            server.WriteApplicationData(new byte[] { 42 }, 0, 1);
            flightWrite = peer.WriteAsync(new ReadOnlySequence<byte>(DrainOutput(server)), deadline.Token).AsTask();
            ReadResult received = await up.Reverse().ReadAsync(1, token: deadline.Token);
            Assert.That(received.Data.ToArray(), Is.EqualTo(new byte[] { 42 }));
            Assert.That(await flightWrite, Is.EqualTo(IOResult.Ok));
            applicationWrite = up.Reverse().WriteAsync(new ReadOnlySequence<byte>(new byte[] { 43 }), deadline.Token).AsTask();
            await OfferRecordAsync(server, peer, deadline.Token);
            byte[] response = new byte[1];
            Assert.That(server.ReadInput(response, 0, 1), Is.EqualTo(1));
            Assert.That(response, Is.EqualTo(new byte[] { 43 }));
            Assert.That(await applicationWrite, Is.EqualTo(IOResult.Ok));
        }
        finally
        {
            deadline.Cancel();
            await up.AbortAsync();
            await down.AbortAsync();
            foreach (Task? task in new Task?[] { client, flightWrite, applicationWrite })
            {
                if (task is null) continue;
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
            server.Close();
        }
    }

    private static byte[] DrainOutput(BcProtocol engine)
    {
        byte[] bytes = new byte[engine.GetAvailableOutputBytes()];
        Assert.That(engine.ReadOutput(bytes, 0, bytes.Length), Is.EqualTo(bytes.Length));
        return bytes;
    }

    private static async Task OfferRecordAsync(BcProtocol engine, IChannel channel, CancellationToken token)
    {
        ReadResult header = await channel.ReadAsync(5, token: token);
        Assert.That(header.Result, Is.EqualTo(IOResult.Ok));
        byte[] prefix = header.Data.ToArray();
        byte[] record = new byte[engine.PreviewInputRecord(prefix).RecordSize];
        prefix.CopyTo(record, 0);
        ReadResult body = await channel.ReadAsync(record.Length - 5, token: token);
        Assert.That(body.Result, Is.EqualTo(IOResult.Ok));
        body.Data.CopyTo(record.AsSpan(5));
        engine.OfferInput(record);
    }

    private sealed class RetryEngine : TlsServerProtocol
    {
        public int Retries { get; private set; }
        protected override ServerHello Generate13HelloRetryRequest(ClientHello clientHello)
        {
            Retries++;
            return base.Generate13HelloRetryRequest(clientHello);
        }
    }

    private sealed class RetryServer : DefaultTlsServer
    {
        private readonly BcTlsCrypto _crypto;
        private readonly BcCertificate _certificate;
        private readonly AsymmetricKeyParameter _key;
        public bool Complete { get; private set; }
        public bool ClientAuthenticated { get; private set; }
        public RetryServer(X509Certificate2 certificate) : this(certificate, new BcTlsCrypto()) { }
        private RetryServer(X509Certificate2 certificate, BcTlsCrypto crypto) : base(crypto)
        {
            _crypto = crypto;
            _certificate = new BcCertificate([], [new CertificateEntry(crypto.CreateCertificate(certificate.RawData), null)]);
            using ECDsa key = certificate.GetECDsaPrivateKey()!;
            byte[] exported = key.ExportPkcs8PrivateKey();
            try { _key = PrivateKeyFactory.CreateKey(exported); }
            finally { CryptographicOperations.ZeroMemory(exported); }
        }
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        public override int[] GetSupportedGroups() => [NamedGroup.secp256r1];
        public override int GetMaxCertificateChainLength() => 1;
        public override TlsCredentials GetCredentials() => new BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(m_context), _crypto, _key, _certificate,
            SignatureAndHashAlgorithm.GetInstance(Org.BouncyCastle.Tls.HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
        public override Org.BouncyCastle.Tls.CertificateRequest GetCertificateRequest() =>
            new([], TlsUtilities.GetDefaultSupportedSignatureAlgorithms(m_context), null, null);
        public override void NotifyClientCertificate(BcCertificate clientCertificate)
        {
            Assert.That(clientCertificate.Length, Is.EqualTo(1));
            using X509Certificate2 remote = X509CertificateLoader.LoadCertificate(clientCertificate.GetCertificateAt(0).GetEncoded());
            ClientAuthenticated = CertificateHelper.ValidateCertificate(remote, TestPeers.PeerId(1).ToString());
            Assert.That(ClientAuthenticated, Is.True);
        }
        public override void NotifyHandshakeComplete() => Complete = true;
    }

    private sealed class ClientProbe(IChannel inner, bool fragmented) : IChannel
    {
        public TaskCompletionSource CompatibilityWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IOResult>? CompatibilityWrite { get; private set; }
        public CancellationToken CancellationToken => inner.CancellationToken;
        public ValueTask<ReadResult> ReadAsync(int length, ReadBlockingMode mode = ReadBlockingMode.WaitAll, CancellationToken token = default) =>
            inner.ReadAsync(fragmented ? Math.Min(length, 3) : length, mode, token);
        public ValueTask<IOResult> WriteAsync(ReadOnlySequence<byte> bytes, CancellationToken token = default)
        {
            ValueTask<IOResult> write = inner.WriteAsync(bytes, token);
            if (bytes.FirstSpan[0] != ContentType.change_cipher_spec) return write;
            CompatibilityWrite = write.AsTask();
            CompatibilityWriteEntered.TrySetResult();
            return new ValueTask<IOResult>(CompatibilityWrite);
        }
        public ValueTask<IOResult> WriteEofAsync(CancellationToken token = default) => inner.WriteEofAsync(token);
        public ValueTask CloseAsync() => inner.CloseAsync();
        public ValueTask AbortAsync() => inner.AbortAsync();
        public ValueTask CloseReadAsync() => inner.CloseReadAsync();
        public TaskAwaiter GetAwaiter() => inner.GetAwaiter();
    }
}
