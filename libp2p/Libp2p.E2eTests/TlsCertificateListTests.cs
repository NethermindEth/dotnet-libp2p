// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

extern alias ModernBc;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using TlsProtocol = Nethermind.Libp2p.Protocols.Tls.TlsProtocol;
using NUnit.Framework;
using ModernBc::Org.BouncyCastle.Security;
using ModernBc::Org.BouncyCastle.Tls;
using ModernBc::Org.BouncyCastle.Tls.Crypto;
using ModernBc::Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = ModernBc::Org.BouncyCastle.Tls.Certificate;
using BcRequest = ModernBc::Org.BouncyCastle.Tls.CertificateRequest;
using HashAlgorithm = ModernBc::Org.BouncyCastle.Tls.HashAlgorithm;

namespace Libp2p.E2eTests;

public class TlsCertificateListTests
{
    [TestCase(false, 0)]
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(false, 3)]
    [TestCase(true, 0)]
    [TestCase(true, 1)]
    [TestCase(true, 2)]
    [TestCase(true, 3)]
    public async Task RegisteredTlsRequiresExactlyOneReceivedCertificate(bool receiverIsServer, int shape)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa extraKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 leaf = CertificateHelper.CertificateFromIdentity(key, TestPeers.Identity(2));
        using X509Certificate2 extra = CertificateHelper.CertificateFromIdentity(extraKey, TestPeers.Identity(3));
        Assert.That(CertificateHelper.ValidateCertificate(leaf, TestPeers.PeerId(2).ToString()), Is.True);
        Assert.That(CertificateHelper.ValidateCertificate(extra, TestPeers.PeerId(3).ToString()), Is.True);
        byte[][] expected = shape switch
        {
            0 => [],
            1 => [leaf.RawData],
            2 => [leaf.RawData, extra.RawData],
            _ => [leaf.RawData, leaf.RawData],
        };
        BcTlsCrypto crypto = new();
        BcCertificate credentialsCertificate = new([], [new CertificateEntry(crypto.CreateCertificate(leaf.RawData), null)]);
        BcCertificate transmitted = new([], expected.Select(der => new CertificateEntry(crypto.CreateCertificate(der), null)).ToArray());
        using ECDsa signingKey = leaf.GetECDsaPrivateKey()!;
        byte[] exported = signingKey.ExportPkcs8PrivateKey();
        ModernBc::Org.BouncyCastle.Crypto.AsymmetricKeyParameter privateKey;
        try { privateKey = PrivateKeyFactory.CreateKey(exported); }
        finally { CryptographicOperations.ZeroMemory(exported); }

        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<TlsProtocol>()
            .AddSingleton<ObservedTlsProtocol>()
            .AddLibp2p<ObservedBuilder>(builder => builder.AddProtocol<IncrementNumberTestProtocol>())
            .BuildServiceProvider();
        await using ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create(TestPeers.Identity(1));
        ObservedTlsProtocol observed = services.GetRequiredService<ObservedTlsProtocol>();
        int publishedSessions = 0;
        peer.OnConnected += _ => Interlocked.Increment(ref publishedSessions);
        using TcpListener rawListener = new(IPAddress.Loopback, 0);
        using TcpClient rawPeer = new();
        Task<ISession>? dial = null;
        TcpClient socket;
        if (receiverIsServer)
        {
            await peer.StartListenAsync(["/ip4/127.0.0.1/tcp/0"], deadline.Token);
            int port = peer.ListenAddresses.Single().ToEndPoint().Port;
            await rawPeer.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
            socket = rawPeer;
        }
        else
        {
            rawListener.Start();
            int port = ((IPEndPoint)rawListener.LocalEndpoint).Port;
            dial = peer.DialAsync((Multiaddress)$"/ip4/127.0.0.1/tcp/{port}/p2p/{TestPeers.PeerId(2)}", deadline.Token);
            socket = await rawListener.AcceptTcpClientAsync(deadline.Token);
        }

        using (socket)
        {
            using CancellationTokenRegistration closeOnDeadline = deadline.Token.Register(socket.Dispose);
            NetworkStream stream = socket.GetStream();
            List<byte[]> emitted = [];
            Task? send = null;
            try
            {
                await NegotiateAsync(stream, receiverIsServer, deadline.Token);
                ObservedContext context = await observed.Entered.Task.WaitAsync(deadline.Token);
                ModernBc::Org.BouncyCastle.Tls.TlsProtocol wire = receiverIsServer
                    ? new RecordingClient(stream, transmitted, emitted)
                    : new RecordingServer(stream, transmitted, emitted);
                send = Task.Run(() =>
                {
                    if (wire is RecordingClient client)
                        client.Connect(new WireClient(crypto, credentialsCertificate, privateKey));
                    else
                        ((RecordingServer)wire).Accept(new WireServer(crypto, credentialsCertificate, privateKey));
                    // TLS 1.3 clients can return before the receiver rejects their list.
                    // Sending bytes here must never cause rejected peers to upgrade.
                    if (shape != 1) wire.Stream.WriteByte(42);
                    else
                    {
                        string hello = ReadLine(wire.Stream);
                        Assert.That(hello, Is.EqualTo("/multistream/1.0.0"), "Reach the registered post-TLS muxer selector.");
                    }
                });

                await Task.WhenAny(context.Upgraded.Task, observed.Finished.Task).WaitAsync(deadline.Token);
                if (shape == 1)
                {
                    Assert.That(context.Upgraded.Task.IsCompletedSuccessfully, Is.True, "Valid one-certificate TLS must upgrade.");
                    await send.WaitAsync(deadline.Token);
                    Assert.That(context.State.RemotePublicKey?.ToByteString(), Is.EqualTo(TestPeers.Identity(2).PublicKey.ToByteString()));
                    Assert.That(context.State.RemoteAddress!.GetPeerId(), Is.EqualTo(TestPeers.PeerId(2)));
                }
                else
                {
                    Assert.That(context.Upgraded.Task.IsCompleted, Is.False, "Reject the received list before upgrading.");
                    Exception? failure = await observed.Finished.Task.WaitAsync(deadline.Token);
                    Assert.That(failure, Is.InstanceOf<AuthenticationException>(), "Receiver-side authentication failure is required.");
                    Assert.That(context.State.RemotePublicKey?.ToByteString(), Is.EqualTo(context.InitialKey));
                    Assert.That(context.State.RemoteAddress?.ToString(), Is.EqualTo(context.InitialAddress));
                    Assert.That(publishedSessions, Is.Zero);
                }
            }
            finally
            {
                socket.Dispose();
                await peer.DisposeAsync();
                if (observed.Entered.Task.IsCompletedSuccessfully)
                    await observed.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (send is not null)
                {
                    try { await send.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (IOException) when (shape != 1) { /* The receiver rejects the wire list. */ }
                }
                if (dial is not null)
                {
                    try { await dial.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception ex) when (ex is OperationCanceledException or Nethermind.Libp2p.Core.Exceptions.PeerConnectionException) { }
                }
                Assert.That(emitted, Is.EqualTo(expected), "Capture the final serialized DER list, including duplicate entries.");
                bool upgraded = observed.Entered.Task.IsCompletedSuccessfully && observed.Entered.Task.Result.Upgraded.Task.IsCompleted;
                TestContext.Out.WriteLine($"PRODUCTION_CERTIFICATE_LIST OS={Environment.OSVersion}; runtime={Environment.Version}; receiver={(receiverIsServer ? "server" : "client")}; shape={shape}; wireCount={emitted.Count}; upgraded={upgraded}; publishedSessions={publishedSessions}");
            }
        }
    }

    // The regular builder retains Noise-first ordering. The raw server refuses Noise,
    // so the real multistream negotiator must fall back to the registered TLS path.
    public sealed class ObservedBuilder(IServiceProvider provider) : Libp2pPeerFactoryBuilder(provider)
    {
        protected override ProtocolRef[] Encryption() => base.Encryption()
            .Select(protocol => protocol.Protocol is TlsProtocol ? Get<ObservedTlsProtocol>() : protocol).ToArray();
    }

    public sealed class ObservedTlsProtocol(TlsProtocol protocol) : IConnectionProtocol
    {
        public string Id => protocol.Id;
        public TaskCompletionSource<ObservedContext> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception?> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DialAsync(IChannel channel, IConnectionContext context) => RunAsync(channel, context, false);
        public Task ListenAsync(IChannel channel, IConnectionContext context) => RunAsync(channel, context, true);
        private async Task RunAsync(IChannel channel, IConnectionContext context, bool listener)
        {
            ObservedContext observer = new(context);
            Entered.TrySetResult(observer);
            try
            {
                if (listener) await protocol.ListenAsync(channel, observer);
                else await protocol.DialAsync(channel, observer);
                Finished.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Finished.TrySetResult(ex);
                throw;
            }
        }
    }

    public sealed class ObservedContext(IConnectionContext inner) : IConnectionContext
    {
        public ByteString? InitialKey { get; } = inner.State.RemotePublicKey?.ToByteString();
        public string? InitialAddress { get; } = inner.State.RemoteAddress?.ToString();
        public TaskCompletionSource Upgraded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILocalPeer Peer => inner.Peer;
        public State State => inner.State;
        public string Id => inner.Id;
        public Activity? Activity => inner.Activity;
        public UpgradeOptions? UpgradeOptions => inner.UpgradeOptions;
        public IEnumerable<IProtocol> SubProtocols => inner.SubProtocols;
        public Task DisconnectAsync() => inner.DisconnectAsync();
        public INewSessionContext UpgradeToSession() => inner.UpgradeToSession();
        public INewConnectionContext CreateConnection() => inner.CreateConnection();
        public void ListenerReady(Multiaddress address) => inner.ListenerReady(address);
        public IChannel Upgrade(UpgradeOptions? options = null)
        {
            IChannel channel = inner.Upgrade(options);
            Upgraded.TrySetResult();
            return channel;
        }
        public IChannel Upgrade(IProtocol specificProtocol, UpgradeOptions? options = null) => inner.Upgrade(specificProtocol, options);
        public Task Upgrade(IChannel channel, UpgradeOptions? options = null) => inner.Upgrade(channel, options);
        public Task Upgrade(IChannel channel, IProtocol protocol, UpgradeOptions? options = null) => inner.Upgrade(channel, protocol, options);
    }

    private static async Task NegotiateAsync(Stream stream, bool receiverIsServer, CancellationToken token)
    {
        await WriteLineAsync(stream, "/multistream/1.0.0", token);
        Assert.That(ReadLine(stream), Is.EqualTo("/multistream/1.0.0"));
        if (receiverIsServer)
        {
            await WriteLineAsync(stream, "/tls/1.0.0", token);
            Assert.That(ReadLine(stream), Is.EqualTo("/tls/1.0.0"));
        }
        else
        {
            Assert.That(ReadLine(stream), Is.EqualTo("/noise"));
            await WriteLineAsync(stream, "na", token);
            Assert.That(ReadLine(stream), Is.EqualTo("/tls/1.0.0"));
            await WriteLineAsync(stream, "/tls/1.0.0", token);
        }
    }

    private static Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        byte[] text = Encoding.UTF8.GetBytes(line + "\n");
        Assert.That(text.Length, Is.LessThan(128));
        return stream.WriteAsync(new byte[] { (byte)text.Length }.Concat(text).ToArray(), token).AsTask();
    }

    private static string ReadLine(Stream stream)
    {
        int length = stream.ReadByte();
        if (length is < 1 or > 127) throw new IOException("Invalid multistream line length.");
        byte[] data = new byte[length];
        stream.ReadExactly(data);
        return Encoding.UTF8.GetString(data).TrimEnd('\n');
    }

    private static void Capture(BcCertificate certificate, TlsContext context, List<byte[]> emitted)
    {
        using MemoryStream encoded = new();
        certificate.Encode(context, encoded, null);
        encoded.Position = 0;
        encoded.Position = 1 + encoded.ReadByte();
        int listLength = TlsUtilities.ReadUint24(encoded);
        long end = encoded.Position + listLength;
        while (encoded.Position < end)
        {
            emitted.Add(TlsUtilities.ReadOpaque24(encoded));
            int length = TlsUtilities.ReadUint16(encoded);
            encoded.Position += length;
        }
        Assert.That(encoded.Position, Is.EqualTo(encoded.Length));
    }

    private sealed class RecordingClient(Stream stream, BcCertificate transmitted, List<byte[]> emitted) : TlsClientProtocol(stream)
    {
        protected override void Send13CertificateMessage(BcCertificate certificate)
        {
            Capture(transmitted, Context, emitted);
            base.Send13CertificateMessage(transmitted);
        }
    }

    private sealed class RecordingServer(Stream stream, BcCertificate transmitted, List<byte[]> emitted) : TlsServerProtocol(stream)
    {
        protected override void Send13CertificateMessage(BcCertificate certificate)
        {
            Capture(transmitted, Context, emitted);
            base.Send13CertificateMessage(transmitted);
        }
    }

    private static TlsCredentials Credentials(TlsContext context, BcTlsCrypto crypto, BcCertificate certificate, ModernBc::Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) =>
        new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto, key, certificate,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));

    private sealed class WireServer(BcTlsCrypto crypto, BcCertificate certificate, ModernBc::Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) : DefaultTlsServer(crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        public override TlsCredentials GetCredentials() => Credentials(m_context, crypto, certificate, key);
        public override BcRequest GetCertificateRequest() => new([], [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)], null, null);
        public override void NotifyClientCertificate(BcCertificate clientCertificate) { }
    }

    private sealed class WireClient(BcTlsCrypto crypto, BcCertificate certificate, ModernBc::Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) : DefaultTlsClient(crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        public override TlsAuthentication GetAuthentication() => new Authentication(() => Credentials(m_context, crypto, certificate, key));
    }

    private sealed class Authentication(Func<TlsCredentials> credentials) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
        public TlsCredentials GetClientCredentials(BcRequest certificateRequest) => credentials();
    }
}
