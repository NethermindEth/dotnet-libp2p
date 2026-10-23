// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Libp2p.Core.TestsBase;
using Nethermind.Libp2p.Protocols.Quic;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using HashAlgorithm = Org.BouncyCastle.Tls.HashAlgorithm;
using BcRequest = Org.BouncyCastle.Tls.CertificateRequest;

namespace Nethermind.Libp2p.Protocols.TLS.Tests;

/// <summary>Observes the transmitted Certificate message separately from the platform's built chain.</summary>
public class CertificateListProbeTests
{
    [TestCase(false, 1)]
    [TestCase(false, 2)]
    [TestCase(false, 3)]
    [TestCase(true, 1)]
    [TestCase(true, 2)]
    [TestCase(true, 3)]
    public async Task ReportsWireCertificateListAndCallbackContents(bool dotnetIsServer, int shape)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        using ECDsa localKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa remoteKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa extraKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 local = CertificateHelper.CertificateFromIdentity(localKey, TestPeers.Identity(1));
        using X509Certificate2 remote = CertificateHelper.CertificateFromIdentity(remoteKey, TestPeers.Identity(2));
        using X509Certificate2 extra = CertificateHelper.CertificateFromIdentity(extraKey, TestPeers.Identity(3));
        try
        {
            byte[][] expected = shape switch
            {
                1 => [remote.RawData],
                2 => [remote.RawData, extra.RawData],
                _ => [remote.RawData, remote.RawData],
            };
            BcTlsCrypto crypto = new();
            BcCertificate chain = new([], expected.Select(der => new CertificateEntry(crypto.CreateCertificate(der), null)).ToArray());
            using ECDsa remotePrivateKey = remote.GetECDsaPrivateKey()!;
            var privateKey = PrivateKeyFactory.CreateKey(remotePrivateKey.ExportPkcs8PrivateKey());
            List<byte[]> emitted = [];
            byte[]? callbackLeaf = null;
            byte[][]? elements = null;
            byte[][]? extraStore = null;
            bool validated = false;
            RemoteCertificateValidationCallback validate = (_, certificate, builtChain, _) =>
            {
                callbackLeaf = certificate?.GetRawCertData();
                validated = certificate is X509Certificate2 x509
                    && CertificateHelper.ValidateCertificate(x509, TestPeers.PeerId(2).ToString());
                elements = builtChain!.ChainElements.Select(element => element.Certificate.RawData).ToArray();
                extraStore = builtChain.ChainPolicy.ExtraStore.Select(item => item.RawData).ToArray();
                return validated;
            };
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            using TcpClient sender = new();
            Task connect = sender.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token).AsTask();
            using TcpClient receiver = await listener.AcceptTcpClientAsync(deadline.Token);
            await connect;
            using SslStream ssl = new(receiver.GetStream(), false);
            // Bouncy Castle exposes the final Certificate serialization hook, without
            // OpenSSL/Schannel's sender-side chain building or certificate filtering.
            TlsProtocol wire = dotnetIsServer
                ? new RecordingClient(sender.GetStream(), emitted)
                : new RecordingServer(sender.GetStream(), emitted);
            Task send = Task.Run(() =>
            {
                if (wire is RecordingClient client)
                    client.Connect(new WireClient(crypto, chain, privateKey));
                else
                    ((RecordingServer)wire).Accept(new WireServer(crypto, chain, privateKey));
                wire.Stream.WriteByte(42);
            });
            try
            {
                if (dotnetIsServer)
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = local,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = validate,
                    }, deadline.Token);
                else
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "libp2p",
                        ClientCertificates = [local],
                        LocalCertificateSelectionCallback = (_, _, _, _, _) => local,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = validate,
                    }, deadline.Token);
                byte[] data = new byte[1];
                await ssl.ReadExactlyAsync(data, deadline.Token);
                await send.WaitAsync(deadline.Token);
                Assert.That(data[0], Is.EqualTo(42));
                Assert.That(emitted, Is.EqualTo(expected), "Capture the actual serialized list, including unrelated and duplicate entries.");
                Assert.That(validated, Is.True, "Keep self-signature and identity valid to isolate list cardinality.");
                Assert.That(callbackLeaf, Is.EqualTo(remote.RawData));
                Assert.That(elements, Is.Not.Null);
                Assert.That(extraStore, Is.Not.Null);
                TestContext.Out.WriteLine($"OS={Environment.OSVersion}; runtime={Environment.Version}; receiver={(dotnetIsServer ? "server" : "client")}; wire={emitted.Count}; ChainElements={elements!.Length}; ExtraStore={extraStore!.Length}; leafCopies={extraStore.Count(der => der.SequenceEqual(remote.RawData))}");
                Assert.That(elements, Has.Length.EqualTo(1), "An unrelated or duplicate transmitted certificate does not extend the trust chain.");
                Assert.That(elements![0], Is.EqualTo(remote.RawData));
            }
            finally
            {
                sender.Dispose();
                receiver.Dispose();
                await send.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            // The pinned parent creates named keys; delete only the exact keys created here.
            DeleteOwnedNamedKey(local);
            DeleteOwnedNamedKey(remote);
            DeleteOwnedNamedKey(extra);
        }
    }

    private static void DeleteOwnedNamedKey(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows()) return;
        using ECDsa? key = certificate.GetECDsaPrivateKey();
        if (key is ECDsaCng cng)
        {
            using CngKey handle = cng.Key;
            if (!handle.IsEphemeral) handle.Delete();
        }
    }

    private static void Capture(BcCertificate certificate, TlsContext context, List<byte[]> emitted)
    {
        using MemoryStream encoded = new();
        certificate.Encode(context, encoded, null);
        encoded.Position = 0;
        encoded.Position = 1 + encoded.ReadByte(); // certificate_request_context
        int listLength = TlsUtilities.ReadUint24(encoded);
        long end = encoded.Position + listLength;
        while (encoded.Position < end)
        {
            emitted.Add(TlsUtilities.ReadOpaque24(encoded));
            int extensionsLength = TlsUtilities.ReadUint16(encoded);
            encoded.Position += extensionsLength;
        }
        Assert.That(encoded.Position, Is.EqualTo(encoded.Length));
    }

    private sealed class RecordingServer(Stream stream, List<byte[]> emitted) : TlsServerProtocol(stream)
    {
        protected override void Send13CertificateMessage(BcCertificate certificate)
        {
            Capture(certificate, Context, emitted);
            base.Send13CertificateMessage(certificate);
        }
    }

    private sealed class RecordingClient(Stream stream, List<byte[]> emitted) : TlsClientProtocol(stream)
    {
        protected override void Send13CertificateMessage(BcCertificate certificate)
        {
            Capture(certificate, Context, emitted);
            base.Send13CertificateMessage(certificate);
        }
    }

    private static TlsCredentials Credentials(TlsContext context, BcTlsCrypto crypto, BcCertificate certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) =>
        new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(context), crypto, key, certificate,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));

    private sealed class WireServer(BcTlsCrypto crypto, BcCertificate certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) : DefaultTlsServer(crypto)
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13];
        public override TlsCredentials GetCredentials() => Credentials(m_context, crypto, certificate, key);
        public override BcRequest GetCertificateRequest() => new([], [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)], null, null);
        public override void NotifyClientCertificate(BcCertificate clientCertificate) { }
    }

    private sealed class WireClient(BcTlsCrypto crypto, BcCertificate certificate, Org.BouncyCastle.Crypto.AsymmetricKeyParameter key) : DefaultTlsClient(crypto)
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
